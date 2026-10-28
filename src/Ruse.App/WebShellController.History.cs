using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private readonly List<HistoryEntry> _undoHistory = new();
    private readonly List<HistoryEntry> _redoHistory = new();

    private sealed class HistoryEntry
    {
        public required string Label { get; init; }
        public required Action Undo { get; set; }
        public required Action Redo { get; set; }
        public string? Key { get; init; }
        public long CreatedAt { get; set; }
    }

    private sealed record NodeSnapshot(
        string Id,
        string Kind,
        string Flavor,
        string Title,
        string Subtitle,
        string Accent,
        double X,
        double Y,
        double? Width,
        double? Height,
        string? MicHint,
        SourceKind SourceKind,
        uint? ProcessId,
        string? ProcessName,
        string? DeviceId,
        string? AsioDriver,
        int[] AsioChannels,
        bool IsVirtual,
        bool IsFile,
        string? FilePath,
        AudioFileFormat FileFormat,
        string RecorderMode,
        bool AutoSave,
        List<PresetProcessor> Processors,
        float Volume,
        bool Muted,
        bool Enabled,
        string? TranscriberModel);

    private void PushAction(string label, Action undo, Action redo, string? coalesceKey = null)
    {
        long now = Environment.TickCount64;
        if (coalesceKey != null && _undoHistory.Count > 0)
        {
            var previous = _undoHistory[^1];
            if (previous.Key == coalesceKey && now - previous.CreatedAt < 900)
            {
                previous.Redo = redo;
                previous.CreatedAt = now;
                bool hadRedo = _redoHistory.Count > 0;
                _redoHistory.Clear();
                if (hadRedo) PublishHistory();
                return;
            }
        }

        _undoHistory.Add(new HistoryEntry
        {
            Label = label,
            Undo = undo,
            Redo = redo,
            Key = coalesceKey,
            CreatedAt = now,
        });

        if (_undoHistory.Count > 200)
        {
            _undoHistory.RemoveAt(0);
        }

        _redoHistory.Clear();
        PublishHistory();
    }

    private void Undo()
    {
        if (_undoHistory.Count == 0) return;
        var entry = _undoHistory[^1];
        _undoHistory.RemoveAt(_undoHistory.Count - 1);
        _redoHistory.Add(entry);
        entry.Undo();
        PublishGraph();
        SetStatus($"Undid {entry.Label.ToLowerInvariant()}.");
        PublishHistory();
    }

    private void Redo()
    {
        if (_redoHistory.Count == 0) return;
        var entry = _redoHistory[^1];
        _redoHistory.RemoveAt(_redoHistory.Count - 1);
        _undoHistory.Add(entry);
        entry.Redo();
        PublishGraph();
        SetStatus($"Redid {entry.Label.ToLowerInvariant()}.");
        PublishHistory();
    }

    private void ClearHistory()
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        PublishHistory();
    }

    private void PublishHistory()
    {
        Post("history", new HistoryDto
        {
            CanUndo = _undoHistory.Count > 0,
            CanRedo = _redoHistory.Count > 0,
            UndoLabel = _undoHistory.Count > 0 ? _undoHistory[^1].Label : "",
            RedoLabel = _redoHistory.Count > 0 ? _redoHistory[^1].Label : "",
        });
    }

    private NodeSnapshot CaptureNode(NodeEntry entry)
    {
        var snapshot = new NodeSnapshot(
            entry.Id, entry.Kind, entry.Flavor, entry.Title, entry.Subtitle, entry.Accent,
            entry.X, entry.Y, entry.Width, entry.Height, entry.MicHint, SourceKind.Application, null, null, null, null,
            Array.Empty<int>(), false, false, null, AudioFileFormat.Wav24, RecorderMode.Off.ToString(), false,
            new List<PresetProcessor>(), 1, false, true, null);

        if (entry.Source != null)
        {
            return snapshot with
            {
                SourceKind = entry.Source.Kind,
                ProcessId = entry.Source.ProcessId,
                ProcessName = entry.Source.ProcessName,
                DeviceId = entry.Source.DeviceId,
                AsioDriver = entry.Source.AsioDriver,
                AsioChannels = entry.Source.AsioChannels.ToArray(),
                Volume = entry.Source.Gain,
                Muted = entry.Source.Muted,
            };
        }

        if (entry.Output != null)
        {
            return snapshot with
            {
                DeviceId = entry.Output.DeviceId,
                IsVirtual = entry.MicHint != null,
                IsFile = entry.Output.IsFileRecorder,
                FilePath = entry.Output.IsFileRecorder ? entry.Output.RecordingPath : null,
                FileFormat = entry.Output.RecordingFormat,
                RecorderMode = entry.Output.RecorderMode.ToString(),
                AutoSave = entry.Output.AutoSave,
                Volume = entry.Output.Volume,
                Muted = entry.Output.Muted,
            };
        }

        if (entry.Modifier != null)
        {
            return snapshot with
            {
                Processors = entry.Modifier.Processors.Select(PresetSerializer.SerializeProcessor).ToList(),
                Enabled = entry.Modifier.Enabled,
                TranscriberModel = entry.TranscriberModel,
            };
        }

        return snapshot;
    }

    private NodeEntry? RestoreNodeSnapshot(NodeSnapshot snapshot)
    {
        try
        {
            if (snapshot.Kind == "source")
            {
                var source = new SourceNode(
                    snapshot.Title,
                    snapshot.SourceKind,
                    snapshot.ProcessId,
                    snapshot.DeviceId,
                    snapshot.AsioDriver,
                    snapshot.AsioChannels,
                    processName: snapshot.ProcessName);
                source.Gain = snapshot.Volume;
                source.Muted = snapshot.Muted;
                var sourceEntry = RegisterSource(source, snapshot.X, snapshot.Y, snapshot.Id);
                sourceEntry.Width = snapshot.Width;
                sourceEntry.Height = snapshot.Height;
                return sourceEntry;
            }

            if (snapshot.Kind == "output")
            {
                OutputNode output;
                string? micHint = null;
                if (snapshot.IsFile)
                {
                    output = OutputNode.CreateForFile(snapshot.FilePath ?? string.Empty, snapshot.FileFormat);
                    output.AutoStart = false;
                }
                else if (snapshot.IsVirtual)
                {
                    var info = VirtualDeviceCatalog.ResolveRender(
                        snapshot.DeviceId ?? string.Empty, snapshot.Title, snapshot.MicHint);
                    micHint = info.CaptureFriendlyName;
                    using var enumerator = new MMDeviceEnumerator();
                    output = new OutputNode(enumerator.GetDevice(info.RenderDeviceId));
                }
                else if (AsioCatalog.IsAsioId(snapshot.DeviceId ?? string.Empty))
                {
                    output = new OutputNode(FindAsioDriver(snapshot.DeviceId!));
                }
                else
                {
                    using var enumerator = new MMDeviceEnumerator();
                    output = new OutputNode(enumerator.GetDevice(snapshot.DeviceId ?? string.Empty));
                }

                output.Volume = snapshot.Volume;
                output.Muted = snapshot.Muted;
                output.RecorderMode = Enum.TryParse<RecorderMode>(snapshot.RecorderMode, true, out var mode)
                    ? mode : RecorderMode.Off;
                output.AutoSave = snapshot.AutoSave;
                var outputEntry = RegisterOutput(output, snapshot.X, snapshot.Y, micHint, snapshot.Id);
                outputEntry.Width = snapshot.Width;
                outputEntry.Height = snapshot.Height;
                return outputEntry;
            }

            if (snapshot.Kind == "modifier")
            {
                var modifier = new ModifierNode(snapshot.Title);
                foreach (var processor in snapshot.Processors)
                {
                    var created = PresetSerializer.CreateProcessor(processor);
                    if (created != null) modifier.Add(created);
                }

                if (modifier.Count == 0 && AudioProcessorCatalog.Create(GainProcessor.Id) is { } fallback)
                {
                    modifier.Add(fallback);
                }

                modifier.Enabled = snapshot.Enabled;
                var modifierEntry = RegisterModifier(modifier, snapshot.X, snapshot.Y,
                    snapshot.Flavor, snapshot.Subtitle, snapshot.Id, snapshot.TranscriberModel);
                modifierEntry.Width = snapshot.Width;
                modifierEntry.Height = snapshot.Height;
                return modifierEntry;
            }

            return null;
        }
        catch (Exception ex)
        {
            ShowError($"Could not restore '{snapshot.Title}': {ex.Message}", ex);
            return null;
        }
    }

    private static LinkEntry CloneLink(LinkEntry link) => new()
    {
        Id = link.Id,
        From = link.From,
        To = link.To,
        Kind = link.Kind,
        Gain = link.Gain,
    };

    private void RestoreLink(LinkEntry link)
    {
        if (_links.Any(existing => existing.Id == link.Id) ||
            !_nodes.TryGetValue(link.From, out var from) ||
            !_nodes.TryGetValue(link.To, out var to))
        {
            return;
        }

        ConnectEntries(from, to, link.Gain, publish: false, linkId: link.Id);
    }

    private void RestoreLinks(IEnumerable<LinkEntry> links)
    {
        foreach (var link in links)
        {
            RestoreLink(link);
        }
    }

    private void PushAddNode(NodeEntry entry)
    {
        var snapshot = CaptureNode(entry);
        PushAction("Add node",
            undo: () =>
            {
                RemoveEntryInternal(entry.Id);
                PublishGraph();
            },
            redo: () =>
            {
                RestoreNodeSnapshot(snapshot);
                PublishGraph();
            });
    }

    private void PushRemoveNode(NodeEntry entry, List<LinkEntry> links)
    {
        var snapshot = CaptureNode(entry);
        PushAction("Remove node",
            undo: () =>
            {
                RestoreNodeSnapshot(snapshot);
                RestoreLinks(links);
                PublishGraph();
            },
            redo: () =>
            {
                RemoveEntryInternal(entry.Id);
                PublishGraph();
            });
    }

    private void PushConnect(LinkEntry link)
    {
        PushAction("Connect nodes",
            undo: () =>
            {
                RemoveLinkInternal(link);
                PublishGraph();
            },
            redo: () =>
            {
                RestoreLink(link);
                PublishGraph();
            });
    }

    private void PushDisconnect(LinkEntry link)
    {
        PushAction("Disconnect cable",
            undo: () =>
            {
                RestoreLink(link);
                PublishGraph();
            },
            redo: () =>
            {
                RemoveLinkInternal(link);
                PublishGraph();
            });
    }
}
