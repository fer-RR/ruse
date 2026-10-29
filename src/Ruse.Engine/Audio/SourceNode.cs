using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Ruse.App.Audio;

public enum SourceKind
{
    Application,
    Device,
    SystemLoopback,
    Asio,
    File,
}

/// <summary>A capture source with per-consumer audio copies.</summary>
public sealed class SourceNode : IDisposable
{
    private IDisposable? _capture;
    private volatile float _meterLevel;
    private volatile bool _clipping;
    private int _sourceChannels;
    private int _sourceSampleRate;
    private double _fileDurationSeconds;

    // Every consumer of this source (each output route) takes its own copy of
    // the audio. Sharing one queue would mean two outputs split the signal
    // between them instead of both hearing it.
    private readonly List<Subscription> _subscriptions = new();
    private readonly object _subscriptionLock = new();

    // Everything is normalized to the mixer's 48 kHz stereo format before it
    // reaches the subscribers, so mixed sources never drift out of pitch/channel.
    private FormatConverter? _converter;
    private readonly List<float> _convertScratch = new(8192);

    // WASAPI endpoints deliver the mix format (32-bit float), while the process
    // loopback endpoint is fed a 16-bit PCM WAVEFORMATEX. Both reach the same
    // converter, but they need different decoding on the way in.
    private bool _sourceIsFloat = true;
    private float[] _pcmScratch = new float[0];

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public SourceKind Kind { get; }
    public string Title { get; set; }
    public uint? ProcessId { get; private set; }
    public string? ProcessName { get; set; }
    public string? DeviceId { get; }
    public string? FilePath { get; private set; }

    /// <summary>ASIO driver name when <see cref="Kind"/> is <see cref="SourceKind.Asio"/>.</summary>
    public string? AsioDriver { get; }

    /// <summary>ASIO input channels to capture (first two are used for the stereo mix).</summary>
    public int[] AsioChannels { get; }
    public bool IsRunning => _capture != null && !_faulted &&
        (_capture is not AudioFileCapture file || file.IsRunning);

    /// <summary>Raised when the capture stream stops by itself (device lost, error).</summary>
    public event Action<SourceNode, string>? Faulted;

    private volatile bool _faulted;
    private volatile bool _stopRequested;

    public float Gain { get; set; } = 1.0f;
    public bool Muted { get; set; }
    public float MeterLevel => _meterLevel;
    public bool Clipping => _clipping;

    /// <summary>Total stereo frames captured since <see cref="Start"/> (diagnostics).</summary>
    public long CapturedFrames { get; private set; }

    /// <summary>Bytes allocated by the capture thread while decoding (diagnostics).</summary>
    public long CaptureAllocatedBytes { get; private set; }

    public static bool SupportsApplicationCapture => ProcessLoopbackCapture.IsSupported;

    public int Channels => _sourceChannels;
    public int SampleRate => _sourceSampleRate;

    public SourceNode(string title, SourceKind kind, uint? processId = null, string? deviceId = null,
        string? asioDriver = null, int[]? asioChannels = null, string? processName = null,
        string? filePath = null, bool fileLoop = false)
    {
        Title = title;
        Kind = kind;
        ProcessId = processId;
        ProcessName = processName;
        DeviceId = deviceId;
        FilePath = filePath;
        FileLoop = fileLoop;
        _fileDurationSeconds = !string.IsNullOrWhiteSpace(filePath)
            ? AudioFileCapture.GetDuration(filePath)
            : 0;
        AsioDriver = asioDriver;
        AsioChannels = asioChannels ?? new[] { 0, 1 };
    }

    public bool FileLoop { get; private set; }

    public bool HasFile => Kind == SourceKind.File &&
        !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);

    public void SetFile(string path, bool loop)
    {
        bool wasRunning = IsRunning;
        Stop();
        FilePath = path;
        FileLoop = loop;
        _fileDurationSeconds = AudioFileCapture.GetDuration(path);
        if (wasRunning)
        {
            Start();
        }
    }

    public void SetFileLoop(bool loop)
    {
        FileLoop = loop;
        if (_capture is AudioFileCapture file)
        {
            file.Loop = loop;
        }
    }

    public void RestartFile()
    {
        if (_capture is AudioFileCapture file)
        {
            file.Restart();
            return;
        }

        if (Kind == SourceKind.File)
        {
            Start();
        }
    }

    public double FilePositionSeconds =>
        _capture is AudioFileCapture file ? file.PositionSeconds : 0;

    public double FileDurationSeconds =>
        _capture is AudioFileCapture file ? file.DurationSeconds : _fileDurationSeconds;

    public bool FilePaused => _capture is AudioFileCapture file && file.IsPaused;

    public void PauseFile()
    {
        if (_capture is AudioFileCapture file) file.Pause();
    }

    public void ResumeFile()
    {
        if (_capture is AudioFileCapture file) file.Resume();
    }

    public void SeekFile(double seconds)
    {
        if (_capture is AudioFileCapture file) file.Seek(seconds);
    }

    /// <summary>Points an app source at a new process instance.</summary>
    public void RetargetApplication(uint processId, string? processName = null)
    {
        if (Kind != SourceKind.Application)
        {
            return;
        }

        if (ProcessId == processId)
        {
            if (!string.IsNullOrWhiteSpace(processName)) ProcessName = processName;
            return;
        }

        bool wasRunning = IsRunning;
        if (wasRunning) Stop();
        ProcessId = processId;
        if (!string.IsNullOrWhiteSpace(processName)) ProcessName = processName;
        if (wasRunning) Start();
    }

    /// <summary>One consumer's private copy of this source.</summary>
    public sealed class Subscription : IDisposable
    {
        /// <summary>How much audio may pile up before the stale head is dropped.</summary>
        private const double StaleLimitMs = 150;

        /// <summary>How much audio is kept when the stale head is dropped.</summary>
        private const double KeepMs = 50;

        private readonly FloatRingBuffer _buffer = new(1_000_000);
        private SourceNode? _owner;

        internal Subscription(SourceNode owner) => _owner = owner;

        /// <summary>Frames dropped because the consumer fell behind (diagnostics).</summary>
        public long SkippedFrames { get; private set; }

        /// <summary>How much audio is waiting, in ms.</summary>
        public double BufferedMs => _buffer.AvailableToRead / 2.0 * 1000.0 / FormatConverter.TargetSampleRate;

        internal void Write(ReadOnlySpan<float> samples) => _buffer.Write(samples);

        /// <summary>Reads the next block of audio for this consumer.</summary>
        public int Read(Span<float> destination)
        {
            // If the consumer fell behind (a stall, a starved render thread),
            // the queue holds audio that is now in the past. Playing it would
            // add that delay for good, so the stale head is dropped and the
            // live edge is played instead.
            int maxFrames = (int)(StaleLimitMs * FormatConverter.TargetSampleRate / 1000.0);
            int keepFrames = (int)(KeepMs * FormatConverter.TargetSampleRate / 1000.0);
            int available = _buffer.AvailableToRead / 2;
            if (available > maxFrames)
            {
                SkippedFrames += _buffer.Skip((available - keepFrames) * 2) / 2;
            }

            return _buffer.Read(destination);
        }

        public void Dispose()
        {
            SourceNode? owner = _owner;
            _owner = null;
            owner?.RemoveSubscription(this);
        }
    }

    /// <summary>Creates a private copy of this source for one consumer.</summary>
    public Subscription Subscribe()
    {
        var subscription = new Subscription(this);
        lock (_subscriptionLock)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    private void RemoveSubscription(Subscription subscription)
    {
        lock (_subscriptionLock)
        {
            _subscriptions.Remove(subscription);
        }
    }

    /// <summary>Hands a block of converted audio to every consumer.</summary>
    private void Publish(ReadOnlySpan<float> samples)
    {
        lock (_subscriptionLock)
        {
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                _subscriptions[i].Write(samples);
            }
        }
    }

    public void Start()
    {
        if (Kind == SourceKind.Application && !SupportsApplicationCapture)
        {
            _faulted = true;
            return;
        }

        if (_capture is AudioFileCapture ended && !ended.IsRunning)
        {
            ended.Dispose();
            _capture = null;
        }

        if (_capture != null)
        {
            return;
        }

        if (Kind == SourceKind.File && !HasFile)
        {
            return;
        }

        _faulted = false;
        _stopRequested = false;

        switch (Kind)
        {
            case SourceKind.Application:
                StartApplicationCapture();
                break;
            case SourceKind.Device:
                StartDeviceCapture();
                break;
            case SourceKind.SystemLoopback:
                StartSystemLoopback();
                break;
            case SourceKind.Asio:
                StartAsioCapture();
                break;
            case SourceKind.File:
                StartFileCapture();
                break;
        }
    }

    private void WireStopEvents(object capture)
    {
        switch (capture)
        {
            case ProcessLoopbackCapture plc:
                plc.RecordingStopped += OnCaptureStopped;
                break;
            case DeviceCapture dc:
                dc.RecordingStopped += OnCaptureStopped;
                break;
            case WasapiRecorder wlc:
                wlc.RecordingStopped += OnCaptureStopped;
                break;
            case AsioCapture ac:
                ac.Stopped += OnAsioStopped;
                break;
        }
    }

    private void OnAsioStopped(AsioCapture capture, string reason)
    {
        if (_stopRequested)
        {
            return;
        }

        _faulted = true;
        Faulted?.Invoke(this, reason);
    }

    private void OnCaptureStopped(object? sender, NAudio.Wave.StoppedEventArgs e)
    {
        if (_stopRequested)
        {
            return;
        }

        _faulted = true;
        string reason = e.Exception?.Message ?? "capture stream stopped";
        Faulted?.Invoke(this, reason);
    }

    private void StartApplicationCapture()
    {
        if (!ProcessLoopbackCapture.IsSupported)
        {
            throw new PlatformNotSupportedException("Per-application capture requires Windows 10 build 20348 or later.");
        }

        var capture = new ProcessLoopbackCapture(ProcessId!.Value);

        // The endpoint format is only known once the capture thread has
        // initialized; 48 kHz 16-bit PCM is the provisional assumption and is
        // corrected on the first packet. It has to be in place before the
        // capture starts, otherwise the first packet is decoded with a stale
        // converter.
        SetSourceFormat(2, FormatConverter.TargetSampleRate, isFloat: false);

        capture.DataAvailable += OnDataAvailable;
        WireStopEvents(capture);
        capture.Start();
        _capture = capture;
    }

    private void StartDeviceCapture()
    {
        var capture = new DeviceCapture(DeviceId!);
        SetSourceFormat(capture.WaveFormat.Channels, capture.WaveFormat.SampleRate);
        capture.DataAvailable += OnDataAvailable;
        capture.Start();
        WireStopEvents(capture);
        _capture = capture;
    }

    private void StartFileCapture()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            throw new FileNotFoundException("Choose an audio file for this player.", FilePath);
        }

        var capture = new AudioFileCapture(FilePath, FileLoop);
        _fileDurationSeconds = capture.DurationSeconds;
        SetSourceFormat(capture.WaveFormat.Channels, capture.WaveFormat.SampleRate);
        capture.DataAvailable += OnDataAvailable;
        capture.Start();
        _capture = capture;
    }

    private void StartAsioCapture()
    {
        if (string.IsNullOrWhiteSpace(AsioDriver))
        {
            throw new InvalidOperationException("No ASIO driver was given for this source.");
        }

        var capture = new AsioCapture(AsioDriver, AsioChannels);
        capture.DataAvailable += OnAsioData;

        try
        {
            capture.Start();
        }
        catch
        {
            capture.DataAvailable -= OnAsioData;
            capture.Dispose();
            throw;
        }

        // ASIO delivers float at the driver's own rate; the converter brings it
        // to the mixer's 48 kHz stereo format like every other source.
        SetSourceFormat(capture.ChannelCount, capture.SampleRate);

        _capture = capture;
    }

    private void OnAsioData(AsioCapture capture, byte[] data)
    {
        var args = new WaveInEventArgs(data, data.Length);
        OnDataAvailable(this, args);
    }

    private void StartSystemLoopback()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        // Loopback capture is incompatible with low-latency mode, so use plain
        // event-driven shared mode on the default render endpoint.
        var capture = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLoopbackCapture()
            .Build();

        SetSourceFormat(capture.WaveFormat.Channels, capture.WaveFormat.SampleRate);
        capture.DataAvailable += OnLoopbackData;
        capture.StartRecording();
        WireStopEvents(capture);
        _capture = capture;
    }

    // WasapiRecorder delivers a span; bridge it to the WaveInEventArgs shape the
    // rest of the pipeline uses, with a reusable buffer to avoid per-packet allocs.
    private byte[] _loopbackBuffer = new byte[16384];
    private WaveInEventArgs? _loopbackArgs;

    private void OnLoopbackData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        if (_loopbackBuffer.Length < buffer.Length)
        {
            _loopbackBuffer = new byte[Math.Max(buffer.Length, _loopbackBuffer.Length * 2)];
            _loopbackArgs = null;
        }

        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            Array.Clear(_loopbackBuffer, 0, buffer.Length);
        }
        else
        {
            buffer.CopyTo(_loopbackBuffer);
        }

        _loopbackArgs = new WaveInEventArgs(_loopbackBuffer, buffer.Length);
        OnDataAvailable(this, _loopbackArgs);
    }

    private void SetSourceFormat(int channels, int sampleRate, bool isFloat = true)
    {
        _sourceChannels = channels;
        _sourceSampleRate = sampleRate;
        _sourceIsFloat = isFloat;
        _converter = new FormatConverter(channels, sampleRate);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            Decode(sender, e);
        }
        finally
        {
            CaptureAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        }
    }

    private void Decode(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        // The process loopback endpoint only reveals its real format once the
        // capture thread has initialized, so reconcile it on the first packet.
        if (sender is ProcessLoopbackCapture plc && plc.WaveFormat != null &&
            (plc.WaveFormat.Channels != _sourceChannels ||
             plc.WaveFormat.SampleRate != _sourceSampleRate ||
             (plc.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat) != _sourceIsFloat))
        {
            SetSourceFormat(
                plc.WaveFormat.Channels,
                plc.WaveFormat.SampleRate,
                isFloat: plc.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat);
        }

        var converter = _converter;
        if (converter == null)
        {
            return;
        }

        ReadOnlySpan<float> samples;
        if (_sourceIsFloat)
        {
            samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                e.Buffer.AsSpan(0, e.BytesRecorded));
        }
        else
        {
            // 16-bit PCM â†’ float, in place, without allocating.
            int sampleCount = e.BytesRecorded / 2;
            if (_pcmScratch.Length < sampleCount)
            {
                _pcmScratch = new float[Math.Max(sampleCount, 4096)];
            }

            for (int i = 0; i < sampleCount; i++)
            {
                _pcmScratch[i] = BitConverter.ToInt16(e.Buffer, i * 2) * (1f / 32768f);
            }

            samples = _pcmScratch.AsSpan(0, sampleCount);
        }

        _convertScratch.Clear();
        converter.Convert(samples, _convertScratch);

        if (_convertScratch.Count == 0)
        {
            return;
        }

        Publish(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_convertScratch));

        // Interleaved stereo -> frames.
        CapturedFrames += _convertScratch.Count / 2;

        // Meter and clip flag on the converted signal.
        float sum = 0;
        float peak = 0;
        for (int i = 0; i < _convertScratch.Count; i++)
        {
            float sample = _convertScratch[i];
            sum += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        float rms = MathF.Sqrt(sum / _convertScratch.Count);
        float meter = Math.Min(1.0f, rms * 3f);
        _meterLevel = float.IsFinite(meter) ? meter : 0f;
        _clipping = float.IsFinite(peak) && peak > 1f;
    }

    /// <summary>Captured audio waiting to be consumed, in ms.</summary>
    public double BufferedMs
    {
        get
        {
            double worst = 0;
            lock (_subscriptionLock)
            {
                for (int i = 0; i < _subscriptions.Count; i++)
                {
                    worst = Math.Max(worst, _subscriptions[i].BufferedMs);
                }
            }

            return worst;
        }
    }

    /// <summary>Frames dropped to catch up with the live edge (diagnostics).</summary>
    public long SkippedFrames
    {
        get
        {
            long total = 0;
            lock (_subscriptionLock)
            {
                for (int i = 0; i < _subscriptions.Count; i++)
                {
                    total += _subscriptions[i].SkippedFrames;
                }
            }

            return total;
        }
    }

    /// <summary>Injects samples directly into every consumer (latency self-test).</summary>
    public void WriteTestSamples(ReadOnlySpan<float> samples)
    {
        Publish(samples);
    }

    public void Stop()
    {
        if (_capture == null)
        {
            return;
        }

        _stopRequested = true;

        switch (_capture)
        {
            case ProcessLoopbackCapture plc:
                plc.DataAvailable -= OnDataAvailable;
                plc.Dispose();
                break;
            case DeviceCapture dc:
                dc.DataAvailable -= OnDataAvailable;
                dc.Dispose();
                break;
            case WasapiRecorder wlc:
                wlc.DataAvailable -= OnLoopbackData;
                wlc.StopRecording();
                wlc.Dispose();
                break;
            case AsioCapture ac:
                ac.DataAvailable -= OnAsioData;
                ac.Dispose();
                break;
            case AudioFileCapture file:
                file.DataAvailable -= OnDataAvailable;
                file.Dispose();
                break;
        }

        _capture = null;
        _meterLevel = 0;
        _clipping = false;
        CapturedFrames = 0;
    }

    public void Dispose()
    {
        Stop();

        lock (_subscriptionLock)
        {
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                _subscriptions[i].Dispose();
            }

            _subscriptions.Clear();
        }
    }
}
