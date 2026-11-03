namespace Ruse.App.Audio;

/// <summary>Multiplies the signal by a sine carrier for metallic, bell-like tones.</summary>
public sealed class RingModulatorProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "ring-mod";

    private const int SampleRate = 48000;

    private readonly AudioParameter[] _parameters;

    private volatile float _frequency = 440f;
    private volatile float _mix = 1f;
    private volatile bool _stereo;
    private float _currentFrequency = 440f;
    private float _currentMix = 1f;
    private bool _primed;
    private double _phase;

    public RingModulatorProcessor()
    {
        _parameters = new[]
        {
            new AudioParameter
            {
                Name = "Frequency",
                Min = 1f,
                Max = 5000f,
                Default = 440f,
                Unit = "Hz",
                Logarithmic = true,
                Get = () => _frequency,
                Set = value => _frequency = Math.Clamp(value, 1f, 5000f),
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
    public string DisplayName => "Ring mod";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        float targetFrequency = _frequency;
        float targetMix = _mix;
        if (!_primed)
        {
            _currentFrequency = targetFrequency;
            _currentMix = targetMix;
            _primed = true;
        }

        bool stereo = _stereo;
        int channels = frames > 0 ? Math.Max(1, samples.Length / frames) : 1;

        for (int frame = 0; frame < frames; frame++)
        {
            _currentFrequency = ParameterSmoothing.Approach(_currentFrequency, targetFrequency, ParameterSmoothing.FastAlpha);
            _currentMix = ParameterSmoothing.Approach(_currentMix, targetMix, ParameterSmoothing.FastAlpha);
            double increment = 2.0 * Math.PI * _currentFrequency / SampleRate;
            float mix = _currentMix;
            float sine = (float)Math.Sin(_phase);
            float cosine = (float)Math.Cos(_phase);
            int index = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                float carrier = stereo && channel == 1 ? cosine : sine;
                float dry = samples[index + channel];
                float wet = dry * carrier;
                samples[index + channel] = dry + ((wet - dry) * mix);
            }

            _phase += increment;
            if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
        }
    }

    public void Reset()
    {
        _phase = 0;
        _primed = false;
    }
}
