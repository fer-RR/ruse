namespace Ruse.App.Audio;

public sealed class MetronomeCapture : IDisposable
{
    private const int SampleRate = FormatConverter.TargetSampleRate;
    private const int Channels = FormatConverter.TargetChannels;
    private const int FramesPerBlock = 240;
    private const double ClickSeconds = 0.025;

    private readonly float[] _samples = new float[FramesPerBlock * Channels];
    private readonly byte[] _bytes = new byte[FramesPerBlock * Channels * sizeof(float)];
    private readonly AudioPacketEventArgs _packetArgs = new();
    private Thread? _thread;
    private volatile bool _stop;
    private volatile float _bpm = 120f;
    private int _beatsPerBar = 4;

    public event EventHandler<AudioPacketEventArgs>? DataAvailable;

    public MetronomeCapture(float bpm, int beatsPerBar)
    {
        Set(bpm, beatsPerBar);
    }

    public void Set(float bpm, int beatsPerBar)
    {
        _bpm = Math.Clamp(bpm, 20f, 300f);
        _beatsPerBar = Math.Clamp(beatsPerBar, 1, 16);
    }

    public float[] RenderOffline(int frames) => RenderOffline(frames, 0);

    public float[] RenderOffline(int frames, long startFrame)
    {
        var result = new float[Math.Max(0, frames) * Channels];
        int offset = 0;
        while (offset < frames)
        {
            int block = Math.Min(FramesPerBlock, frames - offset);
            Generate(startFrame + offset, block);
            Array.Copy(_samples, 0, result, offset * Channels, block * Channels);
            offset += block;
        }
        return result;
    }

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _stop = false;
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "MetronomeCapture",
        };
        _thread.Start();
    }

    private void Pump()
    {
        using var mmcss = MmcssScope.Enter();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long produced = 0;

        try
        {
        while (!_stop)
        {
            long due = (long)(clock.Elapsed.TotalSeconds * SampleRate);
            if (produced >= due)
            {
                Thread.Sleep(1);
                continue;
            }

            int frames = (int)Math.Min(FramesPerBlock, Math.Max(1, due - produced));
            Generate(produced, frames);
            _packetArgs.Set(_bytes, frames * Channels * sizeof(float));
            DataAvailable?.Invoke(this, _packetArgs);
            produced += frames;
        }
        }
        catch (Exception ex)
        {
            AudioFaultLog.ReportOutputPumpFault("Metronome", ex);
            _stop = true;
        }
    }

    private void Generate(long produced, int frames)
    {
        double beatFrames = Math.Max(1.0, SampleRate * 60.0 / _bpm);
        int clickFrames = Math.Max(1, (int)(SampleRate * ClickSeconds));
        int beatsPerBar = _beatsPerBar;

        for (int frame = 0; frame < frames; frame++)
        {
            long position = produced + frame;
            long beatIndex = (long)Math.Floor(position / beatFrames);
            long phase = position - (long)Math.Round(beatIndex * beatFrames);
            float value = 0f;
            if (phase >= 0 && phase < clickFrames)
            {
                double envelope = 1.0 - phase / (double)clickFrames;
                envelope *= envelope;
                double frequency = beatIndex % beatsPerBar == 0 ? 1500.0 : 1000.0;
                value = (float)(Math.Sin(2.0 * Math.PI * frequency * phase / SampleRate) * envelope * 0.35);
            }

            _samples[frame * Channels] = value;
            _samples[(frame * Channels) + 1] = value;
        }

        Buffer.BlockCopy(_samples, 0, _bytes, 0, frames * Channels * sizeof(float));
    }

    public void Stop()
    {
        _stop = true;
        Thread? thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            thread.Join(500);
        }
    }

    public void Dispose() => Stop();
}
