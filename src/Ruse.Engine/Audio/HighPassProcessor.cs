namespace Ruse.App.Audio;

/// <summary>
/// Second-order high-pass filter, useful for removing rumble and handling
/// noise. Built on the shared <see cref="BiquadProcessor"/>; the type id and
/// the "Cutoff" parameter name are kept for preset compatibility.
/// </summary>
public sealed class HighPassProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "highpass";

    private readonly BiquadProcessor _filter;

    public HighPassProcessor()
    {
        _filter = new BiquadProcessor(
            BiquadKind.HighPass,
            Id,
            "High-pass",
            defaultFrequency: 80f,
            frequencyParameterName: "Cutoff", hasQ: true);
    }

    public string TypeId => Id;
    public string DisplayName => "High-pass";
    public IReadOnlyList<AudioParameter> Parameters => _filter.Parameters;

    /// <summary>Corner frequency of the filter, in Hz.</summary>
    public float Cutoff
    {
        get => _filter.Frequency;
        set => _filter.Frequency = value;
    }

    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames) => _filter.Process(samples, frames);

    public void Reset() => _filter.Reset();
}
