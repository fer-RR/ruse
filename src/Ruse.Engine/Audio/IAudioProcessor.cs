namespace Ruse.App.Audio;

/// <summary>A real-time effect on a route. Runs lock-free on the render thread.</summary>
public interface IAudioProcessor
{
    string TypeId { get; }
    string DisplayName { get; }

    /// <summary>Processes interleaved 48 kHz stereo in place.</summary>
    void Process(Span<float> samples, int frames);

    void Reset();

    /// <summary>Look-ahead latency in frames per channel.</summary>
    int LatencySamples => 0;
}

/// <summary>A processor that feeds a display or transcription tap from a monitor.</summary>
public interface ITapProcessor
{
    int TapId { get; }
}

/// <summary>
/// A processor whose instance must be shared by every route that uses it.
/// VST3 plugins own native state and cannot be cloned cheaply or safely.
/// </summary>
public interface ISharedProcessor
{
}

/// <summary>Registry of modifier types, keyed by <see cref="IAudioProcessor.TypeId"/>.</summary>
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

        Register(new Descriptor(
            DbMeterProcessor.Id,
            "dB meter",
            "Shows the RMS and peak level in dBFS.",
            () => new DbMeterProcessor()));

        Register(new Descriptor(
            SpectrumProcessor.Id,
            "Spectrum",
            "Shows a smoothed FFT spectrum of this route.",
            () => new SpectrumProcessor()));

        Register(new Descriptor(
            SpectrogramProcessor.Id,
            "Spectrogram",
            "Scrolls a short FFT history of this route.",
            () => new SpectrogramProcessor()));

        Register(new Descriptor(
            TranscriptionProcessor.Id,
            "Transcriber",
            "Transcribes speech from this route offline.",
            () => new TranscriptionProcessor()));

        Register(new Descriptor(
            TranslatorProcessor.Id,
            "Translator",
            "Translates speech from this route into another language, offline.",
            () => new TranslatorProcessor()));

        Register(new Descriptor(
            "lowpass",
            "Low-pass",
            "Removes high-frequency hiss above the cutoff.",
            () => new BiquadProcessor(BiquadKind.LowPass, "lowpass", "Low-pass", 8000f, "Cutoff", hasQ: true)));

        Register(new Descriptor(
            "eq-bell",
            "EQ bell",
            "Boosts or cuts a band of frequencies around the centre.",
            () => new BiquadProcessor(BiquadKind.Peak, "eq-bell", "EQ bell", 1000f, "Frequency", hasGain: true, hasQ: true)));

        Register(new Descriptor(
            "eq-low-shelf",
            "EQ low shelf",
            "Shapes the low end below the corner frequency.",
            () => new BiquadProcessor(BiquadKind.LowShelf, "eq-low-shelf", "EQ low shelf", 200f, "Frequency", hasGain: true)));

        Register(new Descriptor(
            "eq-high-shelf",
            "EQ high shelf",
            "Shapes the high end above the corner frequency.",
            () => new BiquadProcessor(BiquadKind.HighShelf, "eq-high-shelf", "EQ high shelf", 4000f, "Frequency", hasGain: true)));

        Register(new Descriptor(
            GraphicEqualizerProcessor.Id,
            "Graphic EQ",
            "Ten-band ISO octave equalizer with click-free band changes.",
            () => new GraphicEqualizerProcessor()));

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
