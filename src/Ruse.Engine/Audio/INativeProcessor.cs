namespace Ruse.App.Audio;

/// <summary>
/// Marker for processors that can run on a native-rate passthrough chain.
/// These processors are either sample-by-sample (rate-independent) or
/// explicitly reconfigure their analysis for the supplied rate.
/// </summary>
public interface INativeProcessor
{
    /// <summary>Called before a native chain starts or when its format changes.</summary>
    void ConfigureNative(int sampleRate, int channels) { }
}
