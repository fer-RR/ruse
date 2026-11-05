namespace Ruse.App.Audio;

/// <summary>
/// Mid/side utility for stereo balancing and width control. Mid is the centre
/// information, Side is the difference between the channels.
/// </summary>
public sealed class MidSideProcessor : IAudioProcessor, IParameterizedProcessor, INativeProcessor
{
    public const string Id = "mid-side";

    private volatile float _midDb;
    private volatile float _sideDb;
    private volatile float _width = 1f;
    private float _currentMidGain = 1f;
    private float _currentSideGain = 1f;

    private bool _primed;

    public MidSideProcessor()
    {
        Parameters = new[]
        {
            new AudioParameter
            {
                Name = "Mid",
                Min = -24f,
                Max = 24f,
                Default = 0f,
                Unit = "dB",
                Get = () => _midDb,
                Set = value => _midDb = Math.Clamp(value, -24f, 24f),
            },
            new AudioParameter
            {
                Name = "Side",
                Min = -24f,
                Max = 24f,
                Default = 0f,
                Unit = "dB",
                Get = () => _sideDb,
                Set = value => _sideDb = Math.Clamp(value, -24f, 24f),
            },
            new AudioParameter
            {
                Name = "Width",
                Min = 0f,
                Max = 2f,
                Default = 1f,
                Get = () => _width,
                Set = value => _width = Math.Clamp(value, 0f, 2f),
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Mid/Side";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        int channels = frames > 0 ? Math.Max(1, samples.Length / frames) : 1;
        if (channels < 2)
        {
            return;
        }

        float targetMid = MathF.Pow(10f, _midDb / 20f);
        float targetSide = MathF.Pow(10f, _sideDb / 20f) * _width;
        if (!_primed)
        {
            _currentMidGain = targetMid;
            _currentSideGain = targetSide;
            _primed = true;
        }

        for (int frame = 0; frame < frames; frame++)
        {
            _currentMidGain = ParameterSmoothing.Approach(_currentMidGain, targetMid, ParameterSmoothing.FastAlpha);
            _currentSideGain = ParameterSmoothing.Approach(_currentSideGain, targetSide, ParameterSmoothing.FastAlpha);
            float midGain = _currentMidGain;
            float sideGain = _currentSideGain;
            int index = frame * channels;
            float left = samples[index];
            float right = samples[index + 1];
            float mid = (left + right) * 0.5f * midGain;
            float side = (left - right) * 0.5f * sideGain;
            samples[index] = mid + side;
            samples[index + 1] = mid - side;
        }
    }

    public void Reset()
    {
        _primed = false;
    }
}
