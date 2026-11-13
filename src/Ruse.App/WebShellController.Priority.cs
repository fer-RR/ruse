using System.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private ProcessPriorityClass? _savedPriorityClass;
    private bool _audioPriorityBoosted;

    /// <summary>
    /// Raises the process to Above Normal while routing. MMCSS remains the
    /// primary realtime protection; this is only a secondary scheduling boost.
    /// Affinity is deliberately left alone so Windows can use all cores.
    /// </summary>
    internal void SetAudioPriorityBoost(bool enabled)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            if (enabled)
            {
                if (_audioPriorityBoosted)
                {
                    return;
                }

                ProcessPriorityClass current = process.PriorityClass;
                if (current is ProcessPriorityClass.High or ProcessPriorityClass.RealTime)
                {
                    return;
                }

                _savedPriorityClass = current;
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
                _audioPriorityBoosted = true;
            }
            else if (_audioPriorityBoosted)
            {
                if (_savedPriorityClass is { } saved)
                {
                    process.PriorityClass = saved;
                }

                _audioPriorityBoosted = false;
                _savedPriorityClass = null;
            }
        }
        catch
        {
            // Priority is an optimization; never let it break routing.
        }
    }
}
