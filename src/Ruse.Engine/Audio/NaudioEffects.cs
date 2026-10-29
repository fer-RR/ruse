using NAudio.Effects;

namespace Ruse.App.Audio;

/// <summary>
/// Registers the NAudio.Effects suite (MIT, pure managed, already shipped in
/// NAudio.Core) as modifier processors. Adding one is a single line here.
/// </summary>
public static class NaudioEffects
{
    public static void RegisterAll()
    {
        Register("compressor", "Compressor",
            "Evens out loud and quiet passages.", () => new CompressorEffect());

        Register("limiter", "Limiter",
            "Brick-wall ceiling with look-ahead.", () => new LimiterEffect());

        Register("gate", "Gate",
            "Silences the signal below a threshold.", () => new GateEffect());

        Register("deesser", "De-esser",
            "Tames harsh sibilance.", () => new DeEsserEffect());

        Register("delay", "Delay",
            "Echo with feedback and optional ping-pong.", () => new DelayEffect());

        Register("reverb", "Reverb",
            "Algorithmic room reverb (Freeverb).", () => new ReverbEffect());

        Register("chorus", "Chorus",
            "Thickens the signal with a modulated delay.", () => new ChorusEffect());

        Register("saturation", "Saturation",
            "Soft-clip drive with optional oversampling.", () => new SaturationEffect());

        Register("noise-suppression", "Noise suppression",
            "Spectral background-noise reduction for voice.", () => new NoiseSuppressionEffect());

        Register("agc", "AGC",
            "Automatic gain control that levels voice.", () => new AutomaticGainControlEffect());

        // --- Routing and monitoring ---------------------------------------

        Register("pan", "Pan",
            "Places the signal in the stereo field.", () => new PanEffect());

        Register("stereo-width", "Stereo width",
            "Widens or narrows the stereo image.", () => new StereoWidthEffect());

        Register("mono", "Mono",
            "Folds the stereo image down to mono.", () => new MonoMakerEffect());

        Register("dc-block", "DC blocker",
            "Removes DC offset and low-frequency rumble.", () => new DcBlockerEffect());

        Register("comfort-noise", "Comfort noise",
            "Adds a low noise floor so a silent channel stays open.",
            () => new ComfortNoiseEffect());

        // --- Modulation and movement --------------------------------------

        Register("flanger", "Flanger",
            "Sweeping modulated delay for a jet-like tone.", () => new FlangerEffect());

        Register("phaser", "Phaser",
            "Sweeping comb-filter phase shift.", () => new PhaserEffect());

        Register("tremolo", "Tremolo",
            "Amplitude modulation with an optional auto-pan.",
            () => new TremoloEffect());

        // --- Colour and character -----------------------------------------

        Register("bitcrusher", "Bitcrusher",
            "Lo-fi reduction of bit depth and sample rate.", () => new BitCrusherEffect());

        RegisterProcessor("pitch-shift", "Pitch shift",
            "Shifts pitch with about 43 ms latency.",
            () => new PitchShiftProcessor(PitchShiftProcessor.Id, "Pitch shift", 2048));

        RegisterProcessor("pitch-shift-hq", "Pitch shift HQ",
            "Highest-quality pitch shift, with about 85 ms latency.",
            () => new PitchShiftProcessor(PitchShiftProcessor.HqId, "Pitch shift HQ", 4096));

        Register("transient-shaper", "Transient shaper",
            "Shapes the attack and sustain of the signal.", () => new TransientShaperEffect());

        Register("room-reverb", "Room reverb",
            "Larger, smoother room reverb (FDN).", () => new FdnReverbEffect());

        RegisterProcessor(MultibandCompressorProcessor.Id, "Multiband compressor",
            "Independent dynamics for low, mid and high bands.",
            () => new MultibandCompressorProcessor());
    }

    private static void Register(string typeId, string displayName, string description, Func<AudioEffect> factory)
    {
        AudioProcessorCatalog.Register(new AudioProcessorCatalog.Descriptor(
            typeId,
            displayName,
            description,
            () => new NaudioEffectProcessor(typeId, displayName, factory())));
    }

    private static void RegisterProcessor(string typeId, string displayName, string description, Func<IAudioProcessor> factory)
    {
        AudioProcessorCatalog.Register(new AudioProcessorCatalog.Descriptor(
            typeId,
            displayName,
            description,
            factory));
    }
}
