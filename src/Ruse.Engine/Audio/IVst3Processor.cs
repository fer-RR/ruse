namespace Ruse.App.Audio;

/// <summary>
/// Common surface for an in-process or isolated VST3 node. The isolated
/// implementation keeps the native plugin in a child process so a crash cannot
/// take Ruse down with it.
/// </summary>
public interface IVst3Processor : IAudioProcessor, IParameterizedProcessor, ISharedProcessor, IDisposable
{
    string Identifier { get; }
    string PluginName { get; }
    string Vendor { get; }
    string Version { get; }
    string Status { get; }
    string? Error { get; }
    bool IsReady { get; }
    bool HasEditor { get; }
    bool IsEditorOpen { get; }

    event Action? Changed;

    void Start();
    string? CaptureStateBase64();
    bool OpenEditor(string title);
    void CloseEditor();
}
