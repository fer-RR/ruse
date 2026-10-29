using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>Chooses between the in-process and isolated VST3 hosts.</summary>
public static class Vst3ProcessorFactory
{
    /// <summary>When true, VST3 nodes run in a child process.</summary>
    public static bool UseIsolation { get; set; } = true;

    public static IVst3Processor Create(
        string identifier,
        string? stateBase64 = null,
        IReadOnlyDictionary<string, float>? pendingParameters = null,
        bool start = true)
    {
        if (UseIsolation)
        {
            try
            {
                return new IsolatedVst3Processor(identifier, stateBase64, pendingParameters, start);
            }
            catch (Exception ex)
            {
                AppLog.Warning("Could not start the isolated VST3 host; using the in-process host. " + ex.Message);
            }
        }

        return new Vst3Processor(identifier, stateBase64, pendingParameters, start);
    }
}
