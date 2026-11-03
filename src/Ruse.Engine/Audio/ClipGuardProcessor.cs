namespace Ruse.App.Audio;

/// <summary>
/// Insertable peak limiter with a short attack and soft knee. The output-stage
/// guard keeps its fixed safety settings; this node is for deliberate use in a
/// route.
/// </summary>
public sealed class ClipGuardProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "clip-guard";

    private readonly ClipGuard _guard;
    private volatile float _ceilingDb = -0.2f;
    private volatile float _attackMs = 1f;
    private volatile float _releaseMs = 180f;

    public ClipGuardProcessor()
    {
        _guard = new ClipGuard(Linear(_ceilingDb), _attackMs, _releaseMs);
        Parameters = new[]
        {
            new AudioParameter
            {
                Name = "Ceiling",
                Min = -12f,
                Max = 0f,
                Default = -0.2f,
                Unit = "dB",
                Get = () => _ceilingDb,
                Set = value =>
                {
                    _ceilingDb = Math.Clamp(value, -12f, 0f);
                    Apply();
                },
            },
            new AudioParameter
            {
                Name = "Attack",
                Min = 0.1f,
                Max = 20f,
                Default = 1f,
                Unit = "ms",
                Get = () => _attackMs,
                Set = value =>
                {
                    _attackMs = Math.Clamp(value, 0.1f, 20f);
                    Apply();
                },
            },
            new AudioParameter
            {
                Name = "Release",
                Min = 20f,
                Max = 1000f,
                Default = 180f,
                Unit = "ms",
                Get = () => _releaseMs,
                Set = value =>
                {
                    _releaseMs = Math.Clamp(value, 20f, 1000f);
                    Apply();
                },
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Clip guard";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public bool Engaged => _guard.Engaged;
    public float GainReductionDb => _guard.GainReductionDb;

    public void Process(Span<float> samples, int frames) => _guard.Process(samples, frames);

    public void Reset() => _guard.Reset();

    private void Apply() => _guard.Configure(Linear(_ceilingDb), _attackMs, _releaseMs);

    private static float Linear(float db) => MathF.Pow(10f, db / 20f);
}
