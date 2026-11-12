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
    private volatile bool _stopRequested;
    private bool _releasing;

    private volatile float _frequency = 1000f;
    private volatile float _levelDb = -18f;
    private volatile ToneWaveform _waveform = ToneWaveform.Sine;
    private float _currentFrequency = 1000f;
    private float _currentGain = 0.12589254f;
    private float _envelope;
    private float _envelopeTarget = 1f;
    private bool _primed;
    private ToneWaveform? _lastWaveform;
    private ToneWaveform? _pendingWaveform;

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
        _stopRequested = false;
        _releasing = false;
        _envelope = 0f;
        _envelopeTarget = 1f;
        _pendingWaveform = null;
        _lastWaveform = _waveform;
        _primed = false;
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
            if (_stopRequested)
            {
                _releasing = true;
            }

            if (_releasing && _envelope <= 0.001f)
            {
                _stop = true;
                break;
            }

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

        ToneWaveform targetWaveform = _waveform;
        if (_releasing)
        {
            _envelopeTarget = 0f;
        }
        else if (_lastWaveform != targetWaveform && _pendingWaveform != targetWaveform)
        {
            _pendingWaveform = targetWaveform;
            _envelopeTarget = 0f;
        }

        if (!_releasing && _pendingWaveform is { } pending &&
            _lastWaveform != pending && _envelope <= 0.01f)
        {
            _lastWaveform = pending;
            _pendingWaveform = null;
            _envelopeTarget = 1f;
            _oscillator.Reset();
        }

        ToneWaveform waveform = _lastWaveform ?? targetWaveform;

        for (int frame = 0; frame < frames; frame++)
        {
            _currentFrequency = ParameterSmoothing.Approach(_currentFrequency, targetFrequency, ParameterSmoothing.FastAlpha);
            _currentGain = ParameterSmoothing.Approach(_currentGain, targetGain, ParameterSmoothing.FastAlpha);
            _envelope = ParameterSmoothing.Approach(_envelope, _envelopeTarget, ParameterSmoothing.FastAlpha);
            float value = _oscillator.Next(waveform, _currentFrequency) * _currentGain * _envelope;
            _samples[frame * Channels] = value;
            _samples[(frame * Channels) + 1] = value;
        }

        Buffer.BlockCopy(_samples, 0, _bytes, 0, frames * Channels * sizeof(float));
    }

    public void Stop()
    {
        _stopRequested = true;
        Thread? thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            thread.Join(500);
        }

        _stop = true;
    }

    public void Dispose() => Stop();
}
