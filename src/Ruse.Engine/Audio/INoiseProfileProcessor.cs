namespace Ruse.App.Audio;

/// <summary>
/// A processor that can load a recorded noise-floor file and derive a static
/// spectral footprint from it.
/// </summary>
public interface INoiseProfileProcessor
{
    /// <summary>Full path of the loaded noise-floor file, or null.</summary>
    string? NoiseProfilePath { get; }

    /// <summary>Short display name of the loaded noise floor.</summary>
    string NoiseProfileName { get; }

    /// <summary>Last load error, if any.</summary>
    string? NoiseProfileError { get; }

    /// <summary>True when a noise profile is loaded.</summary>
    bool HasNoiseProfile { get; }

    /// <summary>Loads and analyzes a recorded noise-floor audio file.</summary>
    void LoadNoiseProfile(string path);

    /// <summary>Removes the loaded noise profile.</summary>
    void ClearNoiseProfile();
}
