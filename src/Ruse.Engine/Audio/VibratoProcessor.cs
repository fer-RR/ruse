namespace Ruse.App.Audio;

/// <summary>
/// Pitch modulation built on a short, smoothly modulated delay. The delay is
/// never shorter than one sample, so the effect stays stable at any depth.
/// </summary>
public sealed class VibratoProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "vibrato";

    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int MaxDelayMs = 30;
    private const int MaxFrames = SampleRate * MaxDelayMs / 1000;

    private readonly float[] _buffer = new float[MaxFrames * Channels];
    private readonly AudioParameter[] _parameters;

    private volatile float _rate = 5f;
    private volatile float _depthMs = 4f;
    private volatile float _mix = 1f;
    private volatile bool _stereo;
    private float _currentRate = 5f;
    private float _currentDepthMs = 4f;
    private float _currentMix = 1f;
    private bool _primed;

    private int _write;
    private double _phase;

    public VibratoProcessor()
    {
        _parameters = new[]
        {
            new AudioParameter
            {
                Name = "Rate",
                Min = 0.1f,
                Max = 12f,
                Default = 5f,
                Unit = "Hz",
                Get = () => _rate,
                Set = value => _rate = Math.Clamp(value, 0.1f, 12f),
            },
            new AudioParameter
            {
                Name = "Depth",
                Min = 0f,
                Max = 12f,
                Default = 4f,
                Unit = "ms",
                Get = () => _depthMs,
                Set = value => _depthMs = Math.Clamp(value, 0f, 12f),
            },
            new AudioParameter
            {
                Name = "Mix",
                Min = 0f,
                Max = 1f,
                Default = 1f,
                Get = () => _mix,
                Set = value => _mix = Math.Clamp(value, 0f, 1f),
            },
            new AudioParameter
            {
                Name = "Stereo",
                Min = 0f,
                Max = 1f,
                Default = 0f,
                IsToggle = true,
                Get = () => _stereo ? 1f : 0f,
                Set = value => _stereo = value >= 0.5f,
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Vibrato";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        int channels = frames > 0 ? Math.Max(1, samples.Length / frames) : Channels;
        if (channels > Channels) channels = Channels;

        if (!_primed)
        {
            _currentRate = _rate;
            _currentDepthMs = _depthMs;
            _currentMix = _mix;
            _primed = true;
        }

        float targetRate = _rate;
        float targetDepth = _depthMs;
        float targetMix = _mix;
        bool stereo = _stereo;

        for (int frame = 0; frame < frames; frame++)
        {
            _currentRate = ParameterSmoothing.Approach(_currentRate, targetRate, ParameterSmoothing.SlowAlpha);
            _currentDepthMs = ParameterSmoothing.Approach(_currentDepthMs, targetDepth, ParameterSmoothing.SlowAlpha);
            _currentMix = ParameterSmoothing.Approach(_currentMix, targetMix, ParameterSmoothing.FastAlpha);
            float depth = _currentDepthMs;
            float mix = _currentMix;
            double increment = 2.0 * Math.PI * _currentRate / SampleRate;
            float baseDelayMs = depth + 1f;
            int sampleIndex = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                double channelPhase = stereo && channel == 1 ? _phase + (Math.PI / 2.0) : _phase;
                float lfo = (float)Math.Sin(channelPhase);
                float delayFrames = (baseDelayMs + (depth * lfo)) * SampleRate / 1000f;
                if (delayFrames < 1f) delayFrames = 1f;
                if (delayFrames > MaxFrames - 1) delayFrames = MaxFrames - 1;

                float read = _write - delayFrames;
                while (read < 0f) read += MaxFrames;
                int i0 = (int)read;
                float frac = read - i0;
                int i1 = (i0 + 1) % MaxFrames;

                float dry = samples[sampleIndex + channel];
                float wet = (_buffer[(i0 * Channels) + channel] * (1f - frac)) +
                            (_buffer[(i1 * Channels) + channel] * frac);
                samples[sampleIndex + channel] = dry + ((wet - dry) * mix);
                _buffer[(_write * Channels) + channel] = dry;
            }

            _write++;
            if (_write >= MaxFrames) _write = 0;

            _phase += increment;
            if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
        }
    }

    public void Reset()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _write = 0;
        _phase = 0;
        _primed = false;
    }
}
