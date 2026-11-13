namespace Ruse.App.Audio;

/// <summary>Parameters for the procedural sound-generator source.</summary>
public sealed record SynthSettings(
    float Frequency = 110f,
    ToneWaveform Waveform = ToneWaveform.Saw,
    float LevelDb = -18f,
    float Harmonics = 0.3f,
    float DetuneCents = 6f,
    float Noise = 0.05f,
    float CutoffHz = 2200f,
    float Resonance = 0.2f,
    float Movement = 0.15f,
    float LfoRateHz = 0.7f,
    float StereoWidth = 0.4f)
{
    public static SynthSettings Default { get; } = new();

    public SynthSettings Clamped() => new(
        Math.Clamp(Frequency, 20f, 4000f),
        Waveform,
        Math.Clamp(LevelDb, -60f, 0f),
        Math.Clamp(Harmonics, 0f, 1f),
        Math.Clamp(DetuneCents, 0f, 50f),
        Math.Clamp(Noise, 0f, 1f),
        Math.Clamp(CutoffHz, 60f, 14000f),
        Math.Clamp(Resonance, 0f, 1f),
        Math.Clamp(Movement, 0f, 1f),
        Math.Clamp(LfoRateHz, 0.05f, 12f),
        Math.Clamp(StereoWidth, 0f, 1f));
}

/// <summary>
/// Procedural oscillator/noise source with harmonics, detune, a resonant
/// low-pass filter, slow filter movement and stereo spread. Parameters can be
/// changed while the source is running.
/// </summary>
public sealed class SynthGeneratorCapture : IDisposable
{
    private const int SampleRate = FormatConverter.TargetSampleRate;
    private const int Channels = FormatConverter.TargetChannels;
    private const int FramesPerBlock = 240;

    private readonly float[] _samples = new float[FramesPerBlock * Channels];
    private readonly byte[] _bytes = new byte[FramesPerBlock * Channels * sizeof(float)];
    private readonly AudioPacketEventArgs _packetArgs = new();
    private readonly ToneOscillator _left = new(seed: 11);
    private readonly ToneOscillator _right = new(seed: 17);
    private readonly ToneOscillator _harm2 = new(seed: 23);
    private readonly ToneOscillator _harm3 = new(seed: 29);
    private readonly ToneOscillator _noise = new(seed: 31);
    private BiquadState _filterLeft;
    private BiquadState _filterRight;

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _stopRequested;
    private bool _releasing;
    private volatile SynthSettings _settings = SynthSettings.Default;
    private float _currentFrequency = 110f;
    private float _currentLevel = 0.12589254f;
    private float _currentHarmonics;
    private float _currentNoise;
    private float _currentWidth;
    private float _currentDetuneCents;
    private float _currentCutoff;
    private float _currentResonance;
    private float _currentMovement;
    private float _currentLfoRate;
    private float _envelope;
    private float _envelopeTarget = 1f;
    private double _lfoPhase;
    private ToneWaveform? _lastWaveform;
    private ToneWaveform? _pendingWaveform;

    public SynthGeneratorCapture(SynthSettings settings)
    {
        _settings = settings.Clamped();
        _currentFrequency = _settings.Frequency;
        _currentLevel = MathF.Pow(10f, _settings.LevelDb / 20f);
        _currentHarmonics = _settings.Harmonics;
        _currentNoise = _settings.Noise;
        _currentWidth = _settings.StereoWidth;
        _currentDetuneCents = _settings.DetuneCents;
        _currentCutoff = _settings.CutoffHz;
        _currentResonance = _settings.Resonance;
        _currentMovement = _settings.Movement;
        _currentLfoRate = _settings.LfoRateHz;
        _lastWaveform = _settings.Waveform;
        _filterLeft = new BiquadState();
        _filterRight = new BiquadState();
    }

    public event EventHandler<AudioPacketEventArgs>? DataAvailable;

    public void Set(SynthSettings settings) => _settings = settings.Clamped();

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
        _lastWaveform = _settings.Waveform;
        _filterLeft = new BiquadState();
        _filterRight = new BiquadState();
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "SynthGenerator",
        };
        _thread.Start();
    }

    private void Pump()
    {
        using var mmcss = MmcssScope.Enter();
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
        SynthSettings settings = _settings;
        float targetFrequency = settings.Frequency;
        float targetLevel = MathF.Pow(10f, settings.LevelDb / 20f);
        float targetCutoff = settings.CutoffHz;
        _currentHarmonics = ParameterSmoothing.Move(_currentHarmonics, settings.Harmonics, frames);
        _currentNoise = ParameterSmoothing.Move(_currentNoise, settings.Noise, frames);
        _currentWidth = ParameterSmoothing.Move(_currentWidth, settings.StereoWidth, frames);
        _currentDetuneCents = ParameterSmoothing.Move(_currentDetuneCents, settings.DetuneCents, frames);
        _currentCutoff = ParameterSmoothing.Move(_currentCutoff, settings.CutoffHz, frames);
        _currentResonance = ParameterSmoothing.Move(_currentResonance, settings.Resonance, frames);
        _currentMovement = ParameterSmoothing.Move(_currentMovement, settings.Movement, frames);
        _currentLfoRate = ParameterSmoothing.Move(_currentLfoRate, settings.LfoRateHz, frames);
        float detune = MathF.Pow(2f, _currentDetuneCents / 1200f);

        if (_releasing)
        {
            _envelopeTarget = 0f;
        }
        else if (_lastWaveform != settings.Waveform && _pendingWaveform != settings.Waveform)
        {
            _pendingWaveform = settings.Waveform;
            _envelopeTarget = 0f;
        }

        if (!_releasing && _pendingWaveform is { } pending && _lastWaveform != pending && _envelope <= 0.01f)
        {
            _lastWaveform = pending;
            _pendingWaveform = null;
            _envelopeTarget = 1f;
            _left.Reset();
            _right.Reset();
            _harm2.Reset();
            _harm3.Reset();
            _noise.Reset();
        }

        ToneWaveform waveform = _lastWaveform ?? settings.Waveform;

        if (frames <= 0)
        {
            return;
        }

        float lfo = (float)Math.Sin(_lfoPhase * Math.PI * 2.0);
        _lfoPhase += (_currentLfoRate * frames) / SampleRate;
        if (_lfoPhase >= 1.0)
        {
            _lfoPhase -= Math.Floor(_lfoPhase);
        }

        float mod = 1f + (_currentMovement * 0.8f * lfo);
        float cutoff = Math.Clamp(_currentCutoff * mod, 60f, 14000f);
        _filterLeft.Set(cutoff, _currentResonance);
        _filterRight.Set(cutoff, _currentResonance);

        for (int frame = 0; frame < frames; frame++)
        {
            _currentFrequency = ParameterSmoothing.Approach(_currentFrequency, targetFrequency, ParameterSmoothing.FastAlpha);
            _currentLevel = ParameterSmoothing.Approach(_currentLevel, targetLevel, ParameterSmoothing.FastAlpha);
            _envelope = ParameterSmoothing.Approach(_envelope, _envelopeTarget, ParameterSmoothing.FastAlpha);
            float leftWave = _left.Next(waveform, _currentFrequency);
            float rightWave = _right.Next(waveform, _currentFrequency * detune);
            float h2 = _harm2.Next(waveform, _currentFrequency * 2f);
            float h3 = _harm3.Next(waveform, _currentFrequency * 3f);
            float noise = _noise.Next(ToneWaveform.Noise, _currentFrequency);

            float harmonic = _currentHarmonics;
            float left = leftWave + harmonic * ((h2 * 0.35f) + (h3 * 0.18f));
            float right = rightWave + harmonic * ((h2 * 0.35f) + (h3 * 0.18f));
            left = (left * (1f - _currentNoise)) + (noise * _currentNoise);
            right = (right * (1f - _currentNoise)) + (noise * _currentNoise);

            float mid = (left + right) * 0.5f;
            float side = (left - right) * 0.5f * _currentWidth;
            left = mid + side;
            right = mid - side;

            left = _filterLeft.Process(left);
            right = _filterRight.Process(right);
            left = Math.Clamp(left * _currentLevel * _envelope, -1f, 1f);
            right = Math.Clamp(right * _currentLevel * _envelope, -1f, 1f);
            _samples[frame * Channels] = left;
            _samples[(frame * Channels) + 1] = right;
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

    private struct BiquadState
    {
        private float _b0;
        private float _b1;
        private float _b2;
        private float _a1;
        private float _a2;
        private float _z1;
        private float _z2;
        private float _lastCutoff;
        private float _lastResonance;

        public void Set(float cutoff, float resonance)
        {
            if (Math.Abs(cutoff - _lastCutoff) < 0.5f && Math.Abs(resonance - _lastResonance) < 0.001f)
            {
                return;
            }

            _lastCutoff = cutoff;
            _lastResonance = resonance;
            double w0 = 2.0 * Math.PI * cutoff / SampleRate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * (0.5 + resonance * 9.5));
            double a0 = 1.0 + alpha;
            _b0 = (float)((1.0 - cos) * 0.5 / a0);
            _b1 = (float)((1.0 - cos) / a0);
            _b2 = _b0;
            _a1 = (float)(-2.0 * cos / a0);
            _a2 = (float)((1.0 - alpha) / a0);
        }

        public float Process(float input)
        {
            float output = (_b0 * input) + _z1;
            _z1 = (_b1 * input) - (_a1 * output) + _z2;
            _z2 = (_b2 * input) - (_a2 * output);
            return output;
        }
    }
}
