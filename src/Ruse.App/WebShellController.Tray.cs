using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    internal TraySnapshot GetTraySnapshot()
    {
        double seconds = _nodes.Values
            .Select(entry => entry.Output)
            .Where(output => output?.IsRecording == true)
            .Select(output => output!.RecordedSeconds)
            .DefaultIfEmpty(0)
            .Max();
        return new TraySnapshot(_running, _micMuted, OutputNode.MasterMuted, HasActiveRecording, seconds);
    }

    internal void ToggleRoutingFromTray()
    {
        if (_running)
        {
            StopRouting();
        }
        else
        {
            StartRouting();
        }

        PublishTelemetry(force: true);
    }

    internal void ToggleMicMuteFromTray() => SetMicrophoneMute(!_micMuted);

    internal void ToggleMasterMuteFromTray()
    {
        OutputNode.MasterMuted = !OutputNode.MasterMuted;
        PublishTelemetry(force: true);
    }

    internal void OpenMixerFromTray() => Post("tray-open-mixer", new { });

    internal void OpenTimelineFromTray()
    {
        OpenTimeline();
        Post("tray-open-timeline", new { });
    }
}
