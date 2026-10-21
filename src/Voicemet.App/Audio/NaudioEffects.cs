using NAudio.Effects;

namespace Voicemet.App.Audio;

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
    }

    private static void Register(string typeId, string displayName, string description, Func<AudioEffect> factory)
    {
        AudioProcessorCatalog.Register(new AudioProcessorCatalog.Descriptor(
            typeId,
            displayName,
            description,
            () => new NaudioEffectProcessor(typeId, displayName, factory())));
    }
}
