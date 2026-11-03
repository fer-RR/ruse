namespace Ruse.App.Audio;

/// <summary>
/// Ducks this route while a separate key signal is above the threshold. The key
/// is supplied through <see cref="ISidechainProcessor"/> and never reaches the
/// audible output.
/// </summary>
public sealed class SidechainDuckerProcessor : IAudioProcessor, IParameterizedProcessor, ISidechainProcessor
{
    public const string Id = "sidechain-ducker";

    private const int SampleRate = 48000;

    private volatile float _thresholdDb = -30f;
    private volatile float _duckDb = -12f;
    private volatile float _attackMs = 5f;
    private volatile float _releaseMs = 200f;
    private volatile float _mix = 1f;

    private float _keyLevel;
    private float _gain = 1f;

    public string TypeId => Id;
    public string DisplayName => "Sidechain ducker";

    public IReadOnlyList<AudioParameter> Parameters { get; }

    public SidechainDuckerProcessor()
    {
        Parameters = new[]
        {
            new AudioParameter
            {
                Name = "Threshold",
                Min = -60f,
                Max = 0f,
                Default = -30f,
                Unit = "dB",
                Get = () => _thresholdDb,
                Set = v => _thresholdDb = Math.Clamp(v, -60f, 0f),
            },
            new AudioParameter
            {
                Name = "Duck",
                Min = -40f,
                Max = 0f,
                Default = -12f,
                Unit = "dB",
                Get = () => _duckDb,
                Set = v => _duckDb = Math.Clamp(v, -40f, 0f),
            },
            new AudioParameter
            {
                Name = "Attack",
                Min = 0.5f,
                Max = 200f,
                Default = 5f,
                Unit = "ms",
                Logarithmic = true,
                Get = () => _attackMs,
                Set = v => _attackMs = Math.Clamp(v, 0.5f, 200f),
            },
            new AudioParameter
            {
                Name = "Release",
                Min = 10f,
                Max = 2000f,
                Default = 200f,
                Unit = "ms",
                Logarithmic = true,
                Get = () => _releaseMs,
                Set = v => _releaseMs = Math.Clamp(v, 10f, 2000f),
            },
            new AudioParameter
            {
                Name = "Mix",
                Min = 0f,
                Max = 100f,
                Default = 100f,
                Unit = "%",
                Get = () => _mix * 100f,
                Set = v => _mix = Math.Clamp(v / 100f, 0f, 1f),
            },
        };
    }

    public void SetSidechain(ReadOnlySpan<float> samples, int frames)
    {
        float peak = 0f;
        int count = Math.Min(samples.Length, frames * 2);
        for (int i = 0; i < count; i++)
        {
            float abs = MathF.Abs(samples[i]);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        _keyLevel = float.IsFinite(peak) ? Math.Min(1f, peak) : 0f;
    }

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0)
        {
            return;
        }

        float threshold = MathF.Pow(10f, _thresholdDb / 20f);
        float target = _keyLevel >= threshold
            ? MathF.Pow(10f, _duckDb / 20f)
            : 1f;

        float attack = Coefficient(_attackMs);
        float release = Coefficient(_releaseMs);
        float coefficient = target < _gain ? attack : release;
        float mix = _mix;

        int channels = Math.Max(1, samples.Length / frames);
        for (int frame = 0; frame < frames; frame++)
        {
            _gain = target + (_gain - target) * coefficient;
            float applied = 1f + (_gain - 1f) * mix;
            int index = frame * channels;
            samples[index] *= applied;
            if (channels > 1)
            {
                samples[index + 1] *= applied;
            }
        }
    }

    public void Reset()
    {
        _keyLevel = 0f;
        _gain = 1f;
    }

    private static float Coefficient(float milliseconds)
    {
        double seconds = Math.Max(0.0001, milliseconds / 1000.0);
        return MathF.Exp(-1f / (float)(seconds * SampleRate));
    }
}
