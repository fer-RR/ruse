using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private readonly List<HistoryEntry> _undoHistory = new();
    private readonly List<HistoryEntry> _redoHistory = new();
    private int _undoHistoryLimit = 100;
    private int _timelineUndoSteps = 50;
    private long _timelineUndoBytes = 512L * 1024 * 1024;
    private bool _historyWarningPending;
    private string _historyWarningText = "";

    private const long LowMemoryThresholdBytes = 1536L * 1024 * 1024;

    internal void ConfigureHistoryLimits()
    {
        long available = SystemMemory.AvailablePhysicalBytes;
        bool low = available > 0 && available < LowMemoryThresholdBytes;
        SetUndoHistoryLimit(low ? 25 : 100);
        _timelineUndoSteps = low ? 25 : 50;
        _timelineUndoBytes = low ? 256L * 1024 * 1024 : 512L * 1024 * 1024;
        if (low)
        {
            _historyWarningPending = true;
            _historyWarningText =
                "Low available memory: undo history is limited to 25 steps and 256 MB.";
        }
    }

    private void WarnIfHistoryLimited()
    {
        if (!_historyWarningPending) return;
        _historyWarningPending = false;
        AppLog.Warning(_historyWarningText);
        ShowToast(_historyWarningText);
    }

    internal void SetUndoHistoryLimit(int limit)
    {
        _undoHistoryLimit = Math.Clamp(limit, 1, 500);
        bool changed = false;
        while (_undoHistory.Count > _undoHistoryLimit)
        {
            _undoHistory.RemoveAt(0);
            changed = true;
        }

        if (changed)
        {
            PublishHistory();
        }
    }

    private sealed class HistoryEntry
    {
        public required string Label { get; init; }
        public required Action Undo { get; set; }
        public required Action Redo { get; set; }
        public string? Key { get; init; }
        public long CreatedAt { get; set; }
        public bool Structural { get; init; } = true;
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
        string? TranscriberModel,
        string? TranslatorTarget,
        string? TranslatorModel,
        bool FileLoop,
        float ToneFrequency = 1000f,
        ToneWaveform ToneWave = ToneWaveform.Sine,
        float ToneLevelDb = -18f,
        bool Parallel = false,
        float Send = 0.5f,
        double RecorderSilenceStopSeconds = 0,
        double RecorderStartThresholdDb = -40,
        string? TtsText = null,
        string? TtsSourceNodeId = null,
        string? TtsModel = null,
        float TtsSpeed = 1f,
        int NetworkPort = 0,
        string? NetworkHost = null,
        bool IsNetworkOutput = false,
        float FileSpeed = 1f,
        int TtsVoice = 0,
        bool SaveToSamples = false,
        SynthSettings? Synth = null,
        string? ControlId = null,
        bool IsMetronome = false,
        float MetronomeBpm = 120f,
        int MetronomeBeatsPerBar = 4,
        bool IsMixer = false,
        float MixerWidth = 1f,
        bool MixerAutoGain = false,
        float MixerCrossfade = 0f);

    private void PushAction(
        string label, Action undo, Action redo,
        string? coalesceKey = null, bool structural = true)
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
            Structural = structural,
        });

        if (_undoHistory.Count > _undoHistoryLimit)
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
        if (entry.Structural)
        {
            PublishGraph();
        }

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
        if (entry.Structural)
        {
            PublishGraph();
        }

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
            entry.X, entry.Y, entry.Width, entry.Height, entry.MicHint, SourceKind.Application, null, null, null,
            false, false, null, AudioFileFormat.Wav24, RecorderMode.Off.ToString(), false,
            new List<PresetProcessor>(), 1, false, true, null, null, null, false,
            ControlId: entry.ControlId);

        if (entry.Source != null)
        {
            return snapshot with
            {
                SourceKind = entry.Source.Kind,
                ProcessId = entry.Source.ProcessId,
                ProcessName = entry.Source.ProcessName,
                DeviceId = entry.Source.DeviceId,
                FilePath = entry.Source.Kind == SourceKind.Tts ? null : entry.Source.FilePath,
                FileLoop = entry.Source.FileLoop,
                FileSpeed = entry.Source.FileSpeed,
                ToneFrequency = entry.Source.ToneFrequency,
                ToneWave = entry.Source.ToneWave,
                ToneLevelDb = entry.Source.ToneLevelDb,
                IsMetronome = entry.Source.IsMetronome,
                MetronomeBpm = entry.Source.MetronomeBpm,
                MetronomeBeatsPerBar = entry.Source.MetronomeBeatsPerBar,
                Synth = entry.Source.Kind == SourceKind.Synth ? entry.Source.Synth : null,
                TtsText = entry.TtsText,
                TtsSourceNodeId = entry.TtsSourceNodeId,
                TtsModel = entry.TtsModel,
                TtsSpeed = entry.TtsSpeed,
                TtsVoice = entry.TtsVoice,
                NetworkPort = entry.Source.NetworkPort,
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
                SaveToSamples = entry.Output.SaveToSamples,
                RecorderSilenceStopSeconds = entry.Output.RecorderSilenceStopSeconds,
                RecorderStartThresholdDb = entry.Output.RecorderStartThresholdDb,
                IsNetworkOutput = entry.Output.IsNetworkOutput,
                NetworkHost = entry.Output.NetworkHost,
                NetworkPort = entry.Output.NetworkPort,
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
                Parallel = entry.Modifier.Parallel,
                Send = entry.Modifier.Send,
                TranscriberModel = entry.TranscriberModel,
                TranslatorTarget = entry.TranslatorTarget,
                TranslatorModel = entry.TranslatorModel,
                IsMixer = entry.Modifier.IsMixer,
                MixerWidth = entry.Modifier.MixerWidth,
                MixerAutoGain = entry.Modifier.MixerAutoGain,
                MixerCrossfade = entry.Modifier.MixerCrossfade,
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
                RestoreFrozenProxyPath(snapshot.FilePath);
                var source = new SourceNode(
                    snapshot.Title,
                    snapshot.SourceKind,
                    snapshot.ProcessId,
                    snapshot.DeviceId,
                    processName: snapshot.ProcessName,
                    filePath: snapshot.FilePath,
                    fileLoop: snapshot.FileLoop,
                    id: snapshot.Id);
                source.SetFileSpeed(snapshot.FileSpeed);
                if (snapshot.IsMetronome)
                {
                    source.SetMetronome(snapshot.MetronomeBpm, snapshot.MetronomeBeatsPerBar);
                }

                if (snapshot.SourceKind == SourceKind.Tone)
                {
                    source.SetTone(snapshot.ToneFrequency, snapshot.ToneWave, snapshot.ToneLevelDb);
                }
                else if (snapshot.SourceKind == SourceKind.Synth)
                {
                    source.SetSynth(snapshot.Synth ?? SynthSettings.Default);
                }
                else if (snapshot.SourceKind == SourceKind.Network)
                {
                    source.SetNetworkPort(snapshot.NetworkPort);
                }

                source.Gain = snapshot.Volume;
                source.Muted = snapshot.Muted;
                var sourceEntry = RegisterSource(source, snapshot.X, snapshot.Y, snapshot.Id, snapshot.ControlId);
                if (snapshot.SourceKind == SourceKind.Tts)
                {
                    sourceEntry.TtsText = snapshot.TtsText ?? "";
                    sourceEntry.TtsSourceNodeId = snapshot.TtsSourceNodeId;
                    sourceEntry.TtsModel = snapshot.TtsModel ?? _settings.TtsModel;
                    sourceEntry.TtsSpeed = Math.Clamp(snapshot.TtsSpeed <= 0 ? 1f : snapshot.TtsSpeed, 0.5f, 2f);
                    sourceEntry.TtsVoice = Math.Max(0, snapshot.TtsVoice);
                    sourceEntry.TtsStatus = TtsModels.IsInstalled(sourceEntry.TtsModel) ? "Ready" : "No model installed";
                }
                sourceEntry.Width = snapshot.Width;
                sourceEntry.Height = snapshot.Height;
                return sourceEntry;
            }

            if (snapshot.Kind == "output")
            {
                OutputNode output;
                string? micHint = null;
                if (snapshot.IsNetworkOutput)
                {
                    output = OutputNode.CreateForNetwork(snapshot.NetworkHost ?? "127.0.0.1", snapshot.NetworkPort);
                }
                else if (snapshot.IsFile)
                {
                    output = OutputNode.CreateForFile(snapshot.FilePath ?? string.Empty, snapshot.FileFormat);
                    output.AutoStart = !string.Equals(
                        snapshot.RecorderMode, nameof(RecorderMode.Off), StringComparison.OrdinalIgnoreCase);
                }
                else if (snapshot.IsVirtual)
                {
                    var info = VirtualDeviceCatalog.ResolveRender(
                        snapshot.DeviceId ?? string.Empty, snapshot.Title, snapshot.MicHint);
                    micHint = info.CaptureFriendlyName;
                    using var enumerator = new MMDeviceEnumerator();
                    output = new OutputNode(enumerator.GetDevice(info.RenderDeviceId));
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
                output.SaveToSamples = snapshot.SaveToSamples;
                output.RecorderSilenceStopSeconds = snapshot.RecorderSilenceStopSeconds;
                output.RecorderStartThresholdDb = snapshot.RecorderStartThresholdDb;
                var outputEntry = RegisterOutput(output, snapshot.X, snapshot.Y, micHint, snapshot.Id, snapshot.ControlId);
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
                modifier.Parallel = snapshot.Parallel;
                modifier.Send = snapshot.Send;
                modifier.IsMixer = snapshot.IsMixer;
                modifier.MixerWidth = snapshot.MixerWidth;
                modifier.MixerAutoGain = snapshot.MixerAutoGain;
                modifier.MixerCrossfade = snapshot.MixerCrossfade;
                var modifierEntry = RegisterModifier(modifier, snapshot.X, snapshot.Y,
                    snapshot.Flavor, snapshot.Subtitle, snapshot.Id, snapshot.TranscriberModel,
                    snapshot.TranslatorTarget, snapshot.TranslatorModel, snapshot.ControlId);
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
        Pan = link.Pan,
        DelayMs = link.DelayMs,
        Enabled = link.Enabled,
        Solo = link.Solo,
        BusB = link.BusB,
        Inverted = link.Inverted,
        Native = link.Native,
    };

    private void RestoreLink(LinkEntry link)
    {
        if (_links.Any(existing => existing.Id == link.Id) ||
            !_nodes.TryGetValue(link.From, out var from) ||
            !_nodes.TryGetValue(link.To, out var to))
        {
            return;
        }

        var created = ConnectEntries(from, to, link.Gain, publish: false, linkId: link.Id, kind: link.Kind);
        created.Pan = link.Pan;
        created.DelayMs = link.DelayMs;
        created.Enabled = link.Enabled;
        created.Solo = link.Solo;
        created.BusB = link.BusB;
        created.Inverted = link.Inverted;
        ApplyLinkRouting(created, publish: false);
        SyncMixerInput(created);
        if (created.DelayMs > 0)
        {
            _graph.RefreshEngine();
        }
    }

    private void RestoreLinks(IEnumerable<LinkEntry> links)
    {
        foreach (var link in links)
        {
            RestoreLink(link);
        }
    }

    private void PushAddNode(NodeEntry entry, IReadOnlyList<LinkEntry>? links = null)
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
                if (links != null)
                {
                    RestoreLinks(links);
                }
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
