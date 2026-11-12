using System.IO;
using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private void RefreshTimelineAutomationTargets()
    {
        _timelineAutomationTargets.Clear();
        if (_timeline == null)
        {
            return;
        }

        foreach (var track in _timeline.Project.Tracks)
        {
            foreach (var lane in track.Automation)
            {
                if (_nodes.TryGetValue(lane.TargetNodeId, out var entry) &&
                    entry.Modifier != null &&
                    lane.ProcessorIndex >= 0 &&
                    lane.ProcessorIndex < entry.Modifier.Processors.Count &&
                    entry.Modifier.Processors[lane.ProcessorIndex] is IParameterizedProcessor parameterized &&
                    lane.ParameterIndex >= 0 &&
                    lane.ParameterIndex < parameterized.Parameters.Count)
                {
                    _timelineAutomationTargets[(lane.TargetNodeId, lane.ProcessorIndex, lane.ParameterIndex)] =
                        parameterized.Parameters[lane.ParameterIndex];
                }
            }
        }
    }

    private async void AddTimelineClipPath(string trackId, string path, long? startSample)
    {
        EnsureTimeline();
        if (_timeline == null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(trackId) || _timeline.Project.Tracks.All(track => track.Id != trackId))
        {
            trackId = _timeline.Project.Tracks.FirstOrDefault()?.Id ?? "";
        }

        if (string.IsNullOrWhiteSpace(trackId))
        {
            AddTimelineTrack();
            trackId = _timeline.Project.Tracks.LastOrDefault()?.Id ?? "";
        }

        if (string.IsNullOrWhiteSpace(trackId))
        {
            SetStatus("Could not add the dropped audio.", isError: true);
            return;
        }

        bool ok;
        string error;
        try
        {
            TimelineService timeline = _timeline;
            (ok, error) = await Task.Run(() =>
            {
                bool success = timeline.AddClip(trackId, path, out string message, startSample);
                return (success, message);
            });
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }

        if (!ok)
        {
            SetStatus(error, isError: true);
            return;
        }

        SetStatus($"Added {Path.GetFileName(path)} to the timeline.");
        PublishTimeline();
    }

    private void EnsureTimelineSourceRoute(SourceNode source)
    {
        var entry = _nodes.Values.FirstOrDefault(item => ReferenceEquals(item.Source, source));
        if (entry == null || _links.Any(link => link.From == entry.Id))
        {
            return;
        }

        var output = _nodes.Values.FirstOrDefault(item =>
            item.Output is { IsFileRecorder: false, IsNetworkOutput: false });
        if (output != null)
        {
            ConnectNodes(entry.Id, output.Id, "direct");
            SetStatus($"{entry.Title}: reconnected to {output.Title}.");
        }
        else
        {
            SetStatus($"{entry.Title}: connect the track node to an output before playing.", isError: true);
        }
    }

    private void RemoveTimelineTrack(string trackId)
    {
        if (_timeline == null)
        {
            return;
        }

        var track = _timeline.Project.Tracks.FirstOrDefault(item => item.Id == trackId);
        if (track == null)
        {
            return;
        }

        string? sourceId = track.SourceNodeId;
        if (_timeline.RemoveTrack(trackId))
        {
            if (!string.IsNullOrWhiteSpace(sourceId) && _nodes.ContainsKey(sourceId))
            {
                RemoveEntryInternal(sourceId);
            }

            PublishTimeline();
            PublishGraph();
            SetStatus($"Removed timeline track '{track.Name}'.");
        }
    }

    private void ShowTimelineTrackNode(string trackId)
    {
        EnsureTimeline();
        if (_timeline == null)
        {
            return;
        }

        var track = _timeline.Project.Tracks.FirstOrDefault(item => item.Id == trackId);
        if (track == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(track.SourceNodeId) &&
            _nodes.TryGetValue(track.SourceNodeId, out var existing) &&
            existing.Source is { Kind: SourceKind.Timeline })
        {
            PublishGraph();
            SetStatus($"Track node for '{track.Name}' is already on the canvas.");
            return;
        }

        var source = new SourceNode(track.Name, SourceKind.Timeline);
        var entry = RegisterSource(source, 80, 160 + _timeline.Project.Tracks.Count * 120);
        _timeline.AttachSource(track.Id, source);
        var output = _nodes.Values.FirstOrDefault(item =>
            item.Output is { IsFileRecorder: false, IsNetworkOutput: false });
        if (output != null)
        {
            ConnectNodes(entry.Id, output.Id, "direct");
        }

        PublishTimeline();
        PublishGraph();
        SetStatus($"Restored the track node for '{track.Name}'.");
    }
}
