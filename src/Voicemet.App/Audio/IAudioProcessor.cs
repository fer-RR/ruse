namespace Voicemet.App.Audio;

/// <summary>
/// A real-time audio effect applied to a signal as it travels along a route,
/// between a <see cref="SourceNode"/> and an <see cref="OutputNode"/>.
/// </summary>
/// <remarks>
/// Processors run on the output's render thread, so implementations must be
/// allocation-free and lock-free in <see cref="Process"/>. Parameters may be
/// written from the UI thread at any time, so read them into locals once per
/// call. All samples are in the mixer's normalized format: 32-bit float,
/// interleaved stereo, 48 kHz (see <see cref="FormatConverter"/>).
/// </remarks>
public interface IAudioProcessor
{
    /// <summary>Stable identifier used when persisting a modifier chain.</summary>
    string TypeId { get; }

    /// <summary>Human-readable name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Processes <paramref name="samples"/> in place. The span is interleaved
    /// stereo; <paramref name="frames"/> is the number of stereo frames
    /// (i.e. <c>samples.Length / 2</c>).
    /// </summary>
    void Process(Span<float> samples, int frames);

    /// <summary>Resets any internal state (e.g. filter history) when a route starts.</summary>
    void Reset();

    /// <summary>
    /// Processing latency this processor introduces, in frames per channel.
    /// Zero unless the effect needs look-ahead or block/partitioned buffering;
    /// the graph uses this to time-align parallel routes.
    /// </summary>
    int LatencySamples => 0;
}

/// <summary>
/// Registry of available modifier types, keyed by <see cref="IAudioProcessor.TypeId"/>.
/// New effects are added here and immediately become available to the UI.
/// </summary>
public static class AudioProcessorCatalog
{
    public sealed record Descriptor(string TypeId, string DisplayName, string Description, Func<IAudioProcessor> Create);

    private static readonly Dictionary<string, Descriptor> ById = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registered descriptors in display order.</summary>
    public static IReadOnlyList<Descriptor> All => _ordered;

    private static readonly List<Descriptor> _ordered = new();

    static AudioProcessorCatalog()
    {
        Register(new Descriptor(
            GainProcessor.Id,
            "Gain",
            "Adjusts the signal level along this route.",
            () => new GainProcessor()));

        Register(new Descriptor(
            HighPassProcessor.Id,
            "High-pass",
            "Removes low-frequency rumble below the cutoff.",
            () => new HighPassProcessor()));

        NaudioEffects.RegisterAll();
    }

    public static void Register(Descriptor descriptor)
    {
        if (ById.ContainsKey(descriptor.TypeId))
        {
            return;
        }

        ById[descriptor.TypeId] = descriptor;
        _ordered.Add(descriptor);
    }

    public static Descriptor? Find(string typeId) =>
        ById.TryGetValue(typeId, out var d) ? d : null;

    /// <summary>Creates a fresh instance of the given type, or null if unknown.</summary>
    public static IAudioProcessor? Create(string typeId) => Find(typeId)?.Create();
}
