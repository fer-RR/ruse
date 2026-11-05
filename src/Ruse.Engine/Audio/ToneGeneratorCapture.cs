namespace Ruse.App.Audio;

/// <summary>
/// Generates a real-time stereo test signal at the mixer's native format. It
/// behaves like any other capture source, so it can be routed, recorded and
/// processed without a physical device.
/// </summary>
public sealed class ToneGeneratorCapture : IDisposable
{
    private const int SampleRate = FormatConverter.TargetSampleRate;
    private const int Channels = FormatConverter.TargetChannels;
    private const int FramesPerBlock = 240; // 5 ms

    private readonly float[] _samples = new float[FramesPerBlock * Channels];
    private readonly byte[] _bytes = new byte[FramesPerBlock * Channels * sizeof(float)];
    private readonly AudioPacketEventArgs _packetArgs = new();
    private readonly ToneOscillator _oscillator = new(seed: 1);

    private Thread? _thread;
    private volatile bool _stop;

    private volatile float _frequency = 1000f;
    private volatile float _levelDb = -18f;
    private volatile ToneWaveform _waveform = ToneWaveform.Sine;
    private float _currentFrequency = 1000f;
    private float _currentGain = 0.12589254f;
    private bool _primed;
    private ToneWaveform? _lastWaveform;

    public ToneGeneratorCapture(float frequency, ToneWaveform waveform, float levelDb)
    {
        Set(frequency, waveform, levelDb);
    }

    public event EventHandler<AudioPacketEventArgs>? DataAvailable;

    public void Set(float frequency, ToneWaveform waveform, float levelDb)
    {
        _frequency = Math.Clamp(frequency, 20f, 20000f);
        _waveform = waveform;
        _levelDb = Math.Clamp(levelDb, -60f, 0f);
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
            Name = "ToneGenerator",
        };
        _thread.Start();
    }

    private void Pump()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long produced = 0;

        while (!_stop)
        {
            long due = (long)(clock.Elapsed.TotalSeconds * SampleRate);
            if (produced >= due)
            {
                Thread.Sleep(1);
                continue;
            }

            int frames = (int)Math.Min(FramesPerBlock, Math.Max(1, due - produced));
            Generate(frames);
            _packetArgs.Set(_bytes, frames * Channels * sizeof(float));
            DataAvailable?.Invoke(this, _packetArgs);
            produced += frames;
        }
    }

    private void Generate(int frames)
    {
        float targetFrequency = _frequency;
        float targetGain = MathF.Pow(10f, _levelDb / 20f);
        if (!_primed)
        {
            _currentFrequency = targetFrequency;
            _currentGain = targetGain;
            _primed = true;
        }

        ToneWaveform waveform = _waveform;
        if (_lastWaveform != waveform)
        {
            _oscillator.Reset();
            _lastWaveform = waveform;
        }

        for (int frame = 0; frame < frames; frame++)
        {
            _currentFrequency = ParameterSmoothing.Approach(_currentFrequency, targetFrequency, ParameterSmoothing.FastAlpha);
            _currentGain = ParameterSmoothing.Approach(_currentGain, targetGain, ParameterSmoothing.FastAlpha);
            float value = _oscillator.Next(waveform, _currentFrequency) * _currentGain;
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
