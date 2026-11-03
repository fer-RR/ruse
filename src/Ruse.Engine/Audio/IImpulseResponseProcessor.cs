namespace Ruse.App.Audio;

/// <summary>
/// A processor that can load an external impulse response (IR). The UI uses
/// this to offer a file picker and to show which IR is loaded.
/// </summary>
public interface IImpulseResponseProcessor
{
    /// <summary>Full path of the loaded IR, or null for the built-in IR.</summary>
    string? ImpulseResponsePath { get; }

    /// <summary>Short display name of the loaded IR.</summary>
    string ImpulseResponseName { get; }

    /// <summary>Last load error, if any.</summary>
    string? ImpulseResponseError { get; }

    /// <summary>True when the current IR came from a file.</summary>
    bool HasCustomImpulseResponse { get; }

    /// <summary>Loads a WAV/AIFF/FLAC/MP3 file as the impulse response.</summary>
    void LoadImpulseResponse(string path);

    /// <summary>Returns to the built-in synthetic room IR.</summary>
    void ClearImpulseResponse();
}
