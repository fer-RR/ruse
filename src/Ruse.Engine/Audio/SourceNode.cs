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
    Tone,
}

/// <summary>A capture source with per-consumer audio copies.</summary>
public sealed class SourceNode : IDisposable
{
    private IDisposable? _capture;
    private volatile float _meterLevel;
    private volatile float _meterLevelLeft;
    private volatile float _meterLevelRight;
    private volatile float _meterDbLeft = -60f;
    private volatile float _meterDbRight = -60f;
    private volatile bool _clipping;
    private int _sourceChannels;
    private int _sourceSampleRate;
    private int _sourceBitsPerSample = 32;
    private double _fileDurationSeconds;

    // Every consumer of this source (each output route) takes its own copy of
    // the audio. Sharing one queue would mean two outputs split the signal
    // between them instead of both hearing it.
    private readonly List<Subscription> _subscriptions = new();
    private readonly object _subscriptionLock = new();
    private Subscription[] _subscriptionSnapshot = Array.Empty<Subscription>();

    // Everything is normalized to the mixer's 48 kHz stereo format before it
    // reaches the subscribers, so mixed sources never drift out of pitch/channel.
    private FormatConverter? _converter;
    private readonly List<float> _convertScratch = new(16384);

    // WASAPI endpoints deliver the mix format (32-bit float), while the process
    // loopback endpoint is fed a 16-bit PCM WAVEFORMATEX. Both reach the same
    // converter, but they need different decoding on the way in.
    private bool _sourceIsFloat = true;
    private float[] _pcmScratch = new float[16384];

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
    public bool Solo { get; set; }
    public string? SelectedOutputId { get; set; }
    public float MeterLevel => _meterLevel;
    public float MeterLevelLeft => _meterLevelLeft;
    public float MeterLevelRight => _meterLevelRight;
    public float MeterDbLeft => _meterDbLeft;
    public float MeterDbRight => _meterDbRight;
    public bool Clipping => _clipping;

    /// <summary>Total stereo frames captured since <see cref="Start"/> (diagnostics).</summary>
    public long CapturedFrames { get; private set; }

    /// <summary>Raw source frames accepted before conversion (diagnostics/drift).</summary>
    public long SourceFrames => Interlocked.Read(ref _sourceFrames);

    private long _sourceFrames;

    /// <summary>Bytes allocated by the capture thread while decoding (diagnostics).</summary>
    public long CaptureAllocatedBytes { get; private set; }

    public static bool SupportsApplicationCapture => ProcessLoopbackCapture.IsSupported;

    public int Channels => _sourceChannels;
    public int SampleRate => _sourceSampleRate;
    public int BitsPerSample => _sourceBitsPerSample;

    /// <summary>True when a device capture is actually using IAudioClient3 low-latency mode.</summary>
    public bool CaptureLowLatencyActive => _capture is DeviceCapture capture && capture.LowLatencyActive;

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

    public float ToneFrequency { get; private set; } = 1000f;
    public ToneWaveform ToneWave { get; private set; } = ToneWaveform.Sine;
    public float ToneLevelDb { get; private set; } = -18f;

    /// <summary>Updates the test signal. Takes effect immediately when running.</summary>
    public void SetTone(float frequency, ToneWaveform waveform, float levelDb)
    {
        ToneFrequency = Math.Clamp(frequency, 20f, 20000f);
        ToneWave = waveform;
        ToneLevelDb = Math.Clamp(levelDb, -60f, 0f);
        if (_capture is ToneGeneratorCapture tone)
        {
            tone.Set(ToneFrequency, ToneWave, ToneLevelDb);
        }
    }

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

        private const int FadeFrames = 240;
        private readonly FloatRingBuffer _buffer = new(1_000_000);
        private int _fadeRemaining;
        private float _lastLeft;
        private float _lastRight;
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
            bool skipped = false;
            if (available > maxFrames)
            {
                SkippedFrames += _buffer.Skip((available - keepFrames) * 2) / 2;
                skipped = true;
            }

            int read = _buffer.Read(destination);
            if (read <= 0)
            {
                return read;
            }

            if (skipped)
            {
                _fadeRemaining = FadeFrames;
            }

            if (_fadeRemaining > 0)
            {
                int frames = read / 2;
                int fade = Math.Min(frames, _fadeRemaining);
                for (int f = 0; f < fade; f++)
                {
                    float t = 1f - (_fadeRemaining - f) / (float)FadeFrames;
                    int i = f * 2;
                    destination[i] = _lastLeft + (destination[i] - _lastLeft) * t;
                    destination[i + 1] = _lastRight + (destination[i + 1] - _lastRight) * t;
                }

                _fadeRemaining -= fade;
            }

            _lastLeft = destination[read - 2];
            _lastRight = destination[read - 1];
            return read;
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
            _subscriptionSnapshot = _subscriptions.ToArray();
        }

        return subscription;
    }

    private void RemoveSubscription(Subscription subscription)
    {
        lock (_subscriptionLock)
        {
            _subscriptions.Remove(subscription);
            _subscriptionSnapshot = _subscriptions.ToArray();
        }
    }

    /// <summary>Hands a block of converted audio to every consumer.</summary>
    private void Publish(ReadOnlySpan<float> samples)
    {
        var subscriptions = Volatile.Read(ref _subscriptionSnapshot);
        for (int i = 0; i < subscriptions.Length; i++)
        {
            subscriptions[i].Write(samples);
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
            case SourceKind.Tone:
                StartToneCapture();
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
            throw new PlatformNotSupportedException("Per-application capture requires Windows 11 or Windows Server 2022.");
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
        _capture = capture;
        try
        {
            capture.Start();
        }
        catch
        {
            _capture = null;
            capture.DataAvailable -= OnDataAvailable;
            capture.Dispose();
            throw;
        }
    }

    private void StartDeviceCapture()
    {
        var capture = new DeviceCapture(DeviceId!);
        SetSourceFormat(capture.WaveFormat.Channels, capture.WaveFormat.SampleRate,
            isFloat: IsFloatFormat(capture.WaveFormat),
            bitsPerSample: capture.WaveFormat.BitsPerSample);
        capture.DataAvailable += OnDataAvailable;
        WireStopEvents(capture);
        _capture = capture;
        try
        {
            capture.Start();
        }
        catch
        {
            _capture = null;
            capture.DataAvailable -= OnDataAvailable;
            capture.Dispose();
            throw;
        }
    }

    private void StartToneCapture()
    {
        var capture = new ToneGeneratorCapture(ToneFrequency, ToneWave, ToneLevelDb);
        SetSourceFormat(FormatConverter.TargetChannels, FormatConverter.TargetSampleRate);
        capture.DataAvailable += OnDataAvailable;
        _capture = capture;
        try
        {
            capture.Start();
        }
        catch
        {
            _capture = null;
            capture.DataAvailable -= OnDataAvailable;
            capture.Dispose();
            throw;
        }
    }

    private void StartFileCapture()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            throw new FileNotFoundException("Choose an audio file for this player.", FilePath);
        }

        var capture = new AudioFileCapture(FilePath, FileLoop);
        _fileDurationSeconds = capture.DurationSeconds;
        SetSourceFormat(capture.WaveFormat.Channels, capture.WaveFormat.SampleRate,
            bitsPerSample: capture.WaveFormat.BitsPerSample);
        capture.DataAvailable += OnDataAvailable;
        _capture = capture;
        try
        {
            capture.Start();
        }
        catch
        {
            _capture = null;
            capture.DataAvailable -= OnDataAvailable;
            capture.Dispose();
            throw;
        }
    }

    private void StartAsioCapture()
    {
        if (string.IsNullOrWhiteSpace(AsioDriver))
        {
            throw new InvalidOperationException("No ASIO driver was given for this source.");
        }

        var capture = new AsioCapture(AsioDriver, AsioChannels);
        capture.DataAvailable += OnAsioData;
        WireStopEvents(capture);

        // ASIO delivers float at the driver's own rate; the converter brings it
        // to the mixer's 48 kHz stereo format like every other source.
        SetSourceFormat(capture.ChannelCount, capture.SampleRate, bitsPerSample: 32);
        _capture = capture;

        try
        {
            capture.Start();
        }
        catch
        {
            _capture = null;
            capture.DataAvailable -= OnAsioData;
            capture.Dispose();
            throw;
        }
    }

    private readonly AudioPacketEventArgs _asioArgs = new();

    private void OnAsioData(AsioCapture capture, byte[] data)
    {
        _asioArgs.Set(data, data.Length);
        OnDataAvailable(this, _asioArgs);
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
            .WithBufferLength(20)
            .WithLoopbackCapture()
            .Build();

        SetSourceFormat(capture.WaveFormat.Channels, capture.WaveFormat.SampleRate,
            isFloat: IsFloatFormat(capture.WaveFormat),
            bitsPerSample: capture.WaveFormat.BitsPerSample);
        capture.DataAvailable += OnLoopbackData;
        WireStopEvents(capture);
        _capture = capture;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            _capture = null;
            capture.DataAvailable -= OnLoopbackData;
            capture.Dispose();
            throw;
        }
    }

    // WasapiRecorder delivers a span; bridge it to the reusable packet shape the
    // rest of the pipeline uses, with a reusable buffer to avoid per-packet allocs.
    private byte[] _loopbackBuffer = new byte[16384];
    private readonly AudioPacketEventArgs _loopbackArgs = new();

    private void OnLoopbackData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        if (_loopbackBuffer.Length < buffer.Length)
        {
            _loopbackBuffer = new byte[Math.Max(buffer.Length, _loopbackBuffer.Length * 2)];
        }

        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            Array.Clear(_loopbackBuffer, 0, buffer.Length);
        }
        else
        {
            buffer.CopyTo(_loopbackBuffer);
        }

        _loopbackArgs.Set(_loopbackBuffer, buffer.Length);
        OnDataAvailable(this, _loopbackArgs);
    }

    internal static bool IsFloatFormat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        (format is WaveFormatExtensible extensible &&
         extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);

    private void SetSourceFormat(int channels, int sampleRate, bool isFloat = true, int bitsPerSample = 0)
    {
        _sourceChannels = channels;
        _sourceSampleRate = sampleRate;
        _sourceIsFloat = isFloat;
        _sourceBitsPerSample = bitsPerSample > 0 ? bitsPerSample : isFloat ? 32 : 16;
        _converter = new FormatConverter(channels, sampleRate);
    }

    private void OnDataAvailable(object? sender, AudioPacketEventArgs e)
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

    private void Decode(object? sender, AudioPacketEventArgs e)
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
             plc.WaveFormat.BitsPerSample != _sourceBitsPerSample ||
             IsFloatFormat(plc.WaveFormat) != _sourceIsFloat))
        {
            SetSourceFormat(
                plc.WaveFormat.Channels,
                plc.WaveFormat.SampleRate,
                isFloat: IsFloatFormat(plc.WaveFormat),
                bitsPerSample: plc.WaveFormat.BitsPerSample);
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
            int bytesPerSample = Math.Max(1, _sourceBitsPerSample / 8);
            int sampleCount = e.BytesRecorded / bytesPerSample;
            if (_pcmScratch.Length < sampleCount)
            {
                _pcmScratch = new float[Math.Max(sampleCount, 4096)];
            }

            switch (_sourceBitsPerSample)
            {
                case 8:
                    for (int i = 0; i < sampleCount; i++)
                    {
                        _pcmScratch[i] = (e.Buffer[i] - 128) * (1f / 128f);
                    }
                    break;
                case 24:
                    for (int i = 0; i < sampleCount; i++)
                    {
                        int offset = i * 3;
                        int value = e.Buffer[offset] | (e.Buffer[offset + 1] << 8) | (e.Buffer[offset + 2] << 16);
                        if ((value & 0x800000) != 0)
                        {
                            value |= unchecked((int)0xFF000000);
                        }
                        _pcmScratch[i] = value * (1f / 8388608f);
                    }
                    break;
                case 32:
                    for (int i = 0; i < sampleCount; i++)
                    {
                        _pcmScratch[i] = BitConverter.ToInt32(e.Buffer, i * 4) * (1f / 2147483648f);
                    }
                    break;
                default:
                    for (int i = 0; i < sampleCount; i++)
                    {
                        _pcmScratch[i] = BitConverter.ToInt16(e.Buffer, i * 2) * (1f / 32768f);
                    }
                    break;
            }

            samples = _pcmScratch.AsSpan(0, sampleCount);
        }

        int sourceFrames = samples.Length / _sourceChannels;
        if (sourceFrames > 0)
        {
            Interlocked.Add(ref _sourceFrames, sourceFrames);
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

        // Meter and clip flag on the converted stereo signal.
        double sumLeft = 0;
        double sumRight = 0;
        float peak = 0;
        int frames = _convertScratch.Count / 2;
        for (int i = 0; i + 1 < _convertScratch.Count; i += 2)
        {
            float left = _convertScratch[i];
            float right = _convertScratch[i + 1];
            sumLeft += left * left;
            sumRight += right * right;
            peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
        }

        float meterLeft = frames > 0 ? Math.Min(1.0f, MathF.Sqrt((float)(sumLeft / frames)) * 3f) : 0f;
        float meterRight = frames > 0 ? Math.Min(1.0f, MathF.Sqrt((float)(sumRight / frames)) * 3f) : 0f;
        float dbLeft = frames > 0
            ? (float)(20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumLeft / frames))))
            : -60f;
        float dbRight = frames > 0
            ? (float)(20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumRight / frames))))
            : -60f;
        _meterLevelLeft = float.IsFinite(meterLeft) ? meterLeft : 0f;
        _meterLevelRight = float.IsFinite(meterRight) ? meterRight : 0f;
        _meterLevel = Math.Max(_meterLevelLeft, _meterLevelRight);
        _meterDbLeft = float.IsFinite(dbLeft) ? dbLeft : -60f;
        _meterDbRight = float.IsFinite(dbRight) ? dbRight : -60f;
        _clipping = float.IsFinite(peak) && peak > 1f;
    }

    /// <summary>Captured audio waiting to be consumed, in ms.</summary>
    public double BufferedMs
    {
        get
        {
            double worst = 0;
            var subscriptions = Volatile.Read(ref _subscriptionSnapshot);
            for (int i = 0; i < subscriptions.Length; i++)
            {
                worst = Math.Max(worst, subscriptions[i].BufferedMs);
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
            var subscriptions = Volatile.Read(ref _subscriptionSnapshot);
            for (int i = 0; i < subscriptions.Length; i++)
            {
                total += subscriptions[i].SkippedFrames;
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
            case ToneGeneratorCapture tone:
                tone.DataAvailable -= OnDataAvailable;
                tone.Dispose();
                break;
        }

        _capture = null;
        _meterLevel = 0;
        _meterLevelLeft = 0;
        _meterLevelRight = 0;
        _meterDbLeft = -60f;
        _meterDbRight = -60f;
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
            _subscriptionSnapshot = Array.Empty<Subscription>();
        }
    }
}
