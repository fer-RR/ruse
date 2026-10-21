using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Voicemet.App.Audio;

public enum SourceKind
{
    Application,
    Device,
    SystemLoopback,
}

/// <summary>
/// A capture source in the graph. Owns a ring buffer fed by the capture stream.
/// </summary>
public sealed class SourceNode : IDisposable
{
    private IDisposable? _capture;
    private readonly FloatRingBuffer _ringBuffer;
    private volatile float _meterLevel;
    private int _sourceChannels;
    private int _sourceSampleRate;

    // Everything is normalized to the mixer's 48 kHz stereo format before it
    // reaches the ring buffer, so mixed sources never drift out of pitch/channel.
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
    public uint? ProcessId { get; }
    public string? DeviceId { get; }
    public bool IsRunning => _capture != null && !_faulted;

    /// <summary>Raised when the capture stream stops by itself (device lost, error).</summary>
    public event Action<SourceNode, string>? Faulted;

    private volatile bool _faulted;
    private volatile bool _stopRequested;

    public float Gain { get; set; } = 1.0f;
    public bool Muted { get; set; }
    public float MeterLevel => _meterLevel;

    /// <summary>Total stereo frames captured since <see cref="Start"/> (diagnostics).</summary>
    public long CapturedFrames { get; private set; }

    public int Channels => _sourceChannels;
    public int SampleRate => _sourceSampleRate;

    public SourceNode(string title, SourceKind kind, uint? processId = null, string? deviceId = null)
    {
        Title = title;
        Kind = kind;
        ProcessId = processId;
        DeviceId = deviceId;
        _ringBuffer = new FloatRingBuffer(1_000_000);
    }

    public void Start()
    {
        if (_capture != null)
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
        }
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
            // 16-bit PCM → float, in place, without allocating.
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

        _ringBuffer.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_convertScratch));

        // Interleaved stereo → frames.
        CapturedFrames += _convertScratch.Count / 2;

        // Meter on the converted (post-gain-managed) signal.
        float sum = 0;
        for (int i = 0; i < _convertScratch.Count; i++)
        {
            sum += _convertScratch[i] * _convertScratch[i];
        }

        float rms = MathF.Sqrt(sum / _convertScratch.Count);
        _meterLevel = Math.Min(1.0f, rms * 3f);
    }

    /// <summary>Reads up to <paramref name="destination"/> stereo-compatible frames as floats.</summary>
    public int Read(Span<float> destination)
    {
        return _ringBuffer.Read(destination);
    }

    /// <summary>Injects samples directly into the ring buffer (latency self-test).</summary>
    public void WriteTestSamples(ReadOnlySpan<float> samples)
    {
        _ringBuffer.Write(samples);
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
        }

        _capture = null;
        _ringBuffer.Clear();
        _meterLevel = 0;
        CapturedFrames = 0;
    }

    public void Dispose()
    {
        Stop();
    }
}
