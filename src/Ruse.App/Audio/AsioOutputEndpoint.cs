using NAudio.Wave;
using NAudio.Wave.Asio;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Renders the mix to an ASIO device.
/// </summary>
/// <remarks>
/// The mixer always runs at 48 kHz. ASIO drivers own their clock and may refuse
/// to change rate, so the endpoint first tries the mixer's rate and falls back
/// to the driver's own rate with a resampler in between.
/// </remarks>
internal sealed class AsioOutputEndpoint : IDisposable
{
    private const int MixerRate = 48000;

    private AsioDevice? _device;
    private bool _disposed;

    public string DriverName { get; }
    public int SampleRate { get; private set; }
    public int BufferSize { get; private set; }
    public double LatencyMs { get; private set; }
    public string State { get; private set; } = "None";
    public bool UsesResampler { get; private set; }

    /// <summary>True when the driver picked up a clock and is really streaming.</summary>
    public bool Streaming { get; private set; }

    /// <summary>True when the driver came up and is streaming.</summary>
    public bool IsRunning => State == "Running" || State == "Configured";

    public AsioOutputEndpoint(AsioDriverInfo info)
    {
        DriverName = info.Name;
    }

    /// <summary>
    /// Opens the driver and starts playback from <paramref name="mix"/>.
    /// Throws when the driver cannot be opened or initialised.
    /// </summary>
    public void Start(BufferedWaveProvider mix)
    {
        // The driver is exclusive, so make sure nothing is left open from a
        // previous attempt before grabbing it again.
        CloseDevice();

        _device = AsioDevice.Open(DriverName);

        // Prefer the mixer's rate so no resampling happens at all.
        int targetRate = MixerRate;
        if (!SupportsRate(MixerRate))
        {
            targetRate = _device.CurrentSampleRate > 0 ? _device.CurrentSampleRate : MixerRate;
        }

        try
        {
            StartPlayback(mix, targetRate);
        }
        catch (Exception ex) when (targetRate != MixerRate || IsClockError(ex))
        {
            // The driver refused our rate (many virtual drivers have no clock).
            // Retry at the driver's own rate, resampling the mix into it.
            AppLog.Info($"ASIO '{DriverName}' refused {targetRate} Hz ({ex.Message}); retrying at its native rate.");
            CloseDevice();

            _device = AsioDevice.Open(DriverName);
            targetRate = _device.CurrentSampleRate > 0 ? _device.CurrentSampleRate : MixerRate;
            StartPlayback(mix, targetRate);
        }

        SampleRate = targetRate;
        UsesResampler = targetRate != MixerRate;
        BufferSize = SafeReadInt(() => _device!.FramesPerBuffer);

        // A driver only streams once its host provides a clock. Give it a
        // moment, then report whether it is really running so the user is not
        // left with a silent node and no explanation.
        var deadline = Environment.TickCount64 + 600;
        while (Environment.TickCount64 < deadline)
        {
            State = _device.State.ToString();
            if (State == "Running")
            {
                break;
            }

            Thread.Sleep(25);
        }

        Streaming = State == "Running";

        double latencySamples = SafeRead(() => (double)_device!.OutputLatencySamples);
        LatencyMs = SampleRate > 0 ? latencySamples * 1000.0 / SampleRate : 0;
    }

    private void StartPlayback(BufferedWaveProvider mix, int targetRate)
    {
        var provider = new AsioFloatProvider(mix, targetRate);
        var options = new AsioPlaybackOptions
        {
            Source = provider,
            OutputChannels = new[] { 0, 1 },
            AutoStopOnEndOfStream = false,
        };

        _device!.InitPlayback(options);
    }

    private bool SupportsRate(int rate)
    {
        try
        {
            return _device?.IsSampleRateSupported(rate) ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsClockError(Exception ex) =>
        ex.Message.Contains("ASE_NoClock", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("setSampleRate", StringComparison.OrdinalIgnoreCase);

    private static double SafeRead(Func<double> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    private static int SafeReadInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    private void CloseDevice()
    {
        try
        {
            _device?.Dispose();
        }
        catch
        {
        }

        _device = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        State = "Disposed";
        CloseDevice();
    }
}

/// <summary>
/// Pulls the mix out of the render buffer and presents it to the ASIO driver
/// in the driver's own rate and format.
/// </summary>
internal sealed class AsioFloatProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _source;
    private readonly FormatConverter _converter;
    private readonly WaveFormat _format;
    private readonly byte[] _scratch;
    private readonly List<float> _converted = new();
    private readonly byte[] _out;

    public AsioFloatProvider(BufferedWaveProvider source, int targetRate)
    {
        _source = source;
        _format = WaveFormat.CreateIeeeFloatWaveFormat(targetRate, 2);
        _converter = new FormatConverter(2, source.WaveFormat.SampleRate, targetRate);
        _scratch = new byte[source.WaveFormat.AverageBytesPerSecond / 20]; // 50 ms
        _out = new byte[_scratch.Length];
    }

    public WaveFormat WaveFormat => _format;

    public int Read(byte[] buffer, int offset, int count)
    {
        int read = _source.Read(_scratch.AsSpan(0, Math.Min(count, _scratch.Length)));
        if (read <= 0)
        {
            return 0;
        }

        int floats = read / sizeof(float);
        var samples = new float[floats];
        Buffer.BlockCopy(_scratch, 0, samples, 0, floats * sizeof(float));

        _converted.Clear();
        _converter.Convert(samples, _converted);

        int produced = _converted.Count;
        int bytes = produced * sizeof(float);
        if (bytes > count)
        {
            bytes = count;
            produced = count / sizeof(float);
        }

        for (int i = 0; i < produced; i++)
        {
            BitConverter.GetBytes(_converted[i]).CopyTo(buffer, offset + i * sizeof(float));
        }

        return produced * sizeof(float);
    }

    public int Read(Span<byte> buffer)
    {
        var tmp = new byte[buffer.Length];
        int read = Read(tmp, 0, tmp.Length);
        tmp.AsSpan(0, read).CopyTo(buffer);
        return read;
    }
}
