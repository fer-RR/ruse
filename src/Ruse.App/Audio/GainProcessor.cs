namespace Ruse.App.Audio;

/// <summary>
/// A user-adjustable parameter exposed by an <see cref="IAudioProcessor"/> so a
/// generic UI can present a slider (or toggle) without knowing the effect type.
/// </summary>
public sealed class AudioParameter
{
    public required string Name { get; init; }
    public required float Min { get; init; }
    public required float Max { get; init; }
    public float Default { get; init; }

    /// <summary>Suffix shown after the value (e.g. "Hz"); empty for unitless.</summary>
    public string Unit { get; init; } = "";

    /// <summary>Use a logarithmic slider scale (natural for frequency).</summary>
    public bool Logarithmic { get; init; }

    /// <summary>Render as an on/off checkbox instead of a slider.</summary>
    public bool IsToggle { get; init; }

    /// <summary>Reads the current value (safe from any thread).</summary>
    public required Func<float> Get { get; init; }

    /// <summary>Writes a new value (safe from any thread; read by the audio thread).</summary>
    public required Action<float> Set { get; init; }
}

/// <summary>
/// Linear gain applied to every sample. Gain of 1.0 is unity (pass-through).
/// </summary>
public sealed class GainProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "gain";

    // Written from the UI thread, read on the audio thread: volatile keeps the
    // float from being cached in a register across calls.
    private volatile float _gain = 1.0f;

    public string TypeId => Id;
    public string DisplayName => "Gain";

    public float Gain
    {
        get => _gain;
        set => _gain = value;
    }

    public IReadOnlyList<AudioParameter> Parameters { get; }

    public GainProcessor()
    {
        Parameters = new[]
        {
            new AudioParameter
            {
                Name = "Gain",
                Min = 0f,
                Max = 2f,
                Default = 1f,
                Get = () => _gain,
                Set = v => _gain = v,
            },
        };
    }

    public void Process(Span<float> samples, int frames)
    {
        float g = _gain;
        if (g == 1.0f)
        {
            return;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= g;
        }
    }

    public void Reset()
    {
    }
}

/// <summary>
/// Implemented by processors that expose tweakable parameters to the UI.
/// </summary>
public interface IParameterizedProcessor
{
    IReadOnlyList<AudioParameter> Parameters { get; }
}
