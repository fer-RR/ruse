using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private void AddFromMessage(BridgeMessage message)
    {
        string kind = String(message, "kind");
        string id = String(message, "id");
        string title = String(message, "title");
        string linkId = String(message, "linkId");
        double x = Number(message, "x", 60);
        double y = Number(message, "y", 60);
        uint processId = (uint)Number(message, "processId");

        NodeEntry? entry;
        switch (kind)
        {
            case "app":
            {
                var app = _catalog.Apps.FirstOrDefault(item => item.Id == id) ??
                          _catalog.Apps.FirstOrDefault(item => item.ProcessId == processId) ??
                          throw new InvalidOperationException("The application audio session is no longer available. Refresh and try again.");
                entry = RegisterSource(new SourceNode(app.Name, SourceKind.Application, app.ProcessId), x, y);
                SetStatus($"Added source: {app.Name}");
                break;
            }
            case "system":
                entry = RegisterSource(new SourceNode("System audio", SourceKind.SystemLoopback), x, y);
                SetStatus("Added system audio.");
                break;
            case "input": entry = AddDeviceInput(id, x, y); break;
            case "output": entry = AddDeviceOutput(id, x, y); break;
            case "virtual": entry = AddVirtualOutput(id, x, y); break;
            case "effect": entry = AddEffect(id, x, y); break;
            case "chain": entry = AddChain(id, x, y); break;
            case "recorder":
                entry = RegisterOutput(OutputNode.CreateForFile(string.Empty, AudioFileFormat.Wav24), x, y);
                SetStatus("Added a file recorder. Choose a destination to arm it.");
                break;
            default: throw new InvalidOperationException($"Unknown library item '{kind}'.");
        }

        if (entry != null && !string.IsNullOrWhiteSpace(title))
        {
            ApplyRename(entry.Id, title, publish: false);
        }

        if (entry != null && !string.IsNullOrEmpty(linkId) && InsertModifierIntoLink(entry, linkId))
        {
            PublishGraph();
            return;
        }

        if (entry != null)
        {
            PushAddNode(entry);
        }

        PublishGraph();
    }

    /// <summary>Inserts a modifier into a cable as one undo step.</summary>
    private bool InsertModifierIntoLink(NodeEntry modifier, string linkId)
    {
        if (modifier.Modifier == null) return false;
        var link = _links.FirstOrDefault(item => item.Id == linkId);
        if (link == null ||
            !_nodes.TryGetValue(link.From, out var from) ||
            !_nodes.TryGetValue(link.To, out var to))
        {
            return false;
        }

        bool valid = link.Kind switch
        {
            "direct" => from.Source != null && to.Output != null,
            "source-modifier" => from.Source != null && to.Modifier != null,
            "modifier-output" => from.Modifier != null && to.Output != null,
            "modifier-modifier" => from.Modifier != null && to.Modifier != null,
            _ => false,
        };
        if (!valid) return false;

        var oldLink = CloneLink(link);
        RemoveLinkInternal(link);
        var left = ConnectEntries(from, modifier);
        var right = ConnectEntries(modifier, to);
        var nodeSnapshot = CaptureNode(modifier);
        var leftLink = CloneLink(left);
        var rightLink = CloneLink(right);

        PushAction("Insert effect into cable",
            undo: () =>
            {
                RemoveEntryInternal(modifier.Id);
                RestoreLink(oldLink);
                PublishGraph();
            },
            redo: () =>
            {
                RemoveLinkInternal(oldLink);
                RestoreNodeSnapshot(nodeSnapshot);
                RestoreLink(leftLink);
                RestoreLink(rightLink);
                PublishGraph();
            });

        SetStatus($"Inserted '{modifier.Title}' into the cable.");
        return true;
    }

    private NodeEntry AddDeviceInput(string id, double x, double y)
    {
        if (AsioCatalog.IsAsioId(id))
        {
            var driver = FindAsioDriver(id);
            return RegisterSource(new SourceNode(
                driver.Name, SourceKind.Asio, deviceId: id,
                asioDriver: driver.Name, asioChannels: new[] { 0, 1 }), x, y);
        }

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(id);
        var entry = RegisterSource(new SourceNode(device.FriendlyName, SourceKind.Device, deviceId: id), x, y);
        SetStatus($"Added input: {device.FriendlyName}");
        return entry;
    }

    private NodeEntry? AddDeviceOutput(string id, double x, double y)
    {
        if (FindOutputByDevice(id) != null)
        {
            SetStatus("That output is already on the canvas.");
            return null;
        }

        OutputNode output;
        if (AsioCatalog.IsAsioId(id))
        {
            output = new OutputNode(FindAsioDriver(id));
        }
        else
        {
            using var enumerator = new MMDeviceEnumerator();
            output = new OutputNode(enumerator.GetDevice(id));
        }

        var entry = RegisterOutput(output, x, y);
        SetStatus($"Added output: {output.Title}");
        return entry;
    }

    private NodeEntry? AddVirtualOutput(string id, double x, double y)
    {
        if (FindOutputByDevice(id) != null)
        {
            SetStatus("That virtual output is already on the canvas.");
            return null;
        }

        var info = VirtualDeviceCatalog.Discover().FirstOrDefault(item =>
                       string.Equals(item.RenderDeviceId, id, StringComparison.OrdinalIgnoreCase)) ??
                   VirtualDeviceCatalog.ResolveRender(id, "Virtual output", null);
        using var enumerator = new MMDeviceEnumerator();
        var output = new OutputNode(enumerator.GetDevice(info.RenderDeviceId));
        var entry = RegisterOutput(output, x, y, info.CaptureFriendlyName);
        SetStatus($"Added {info.RenderFriendlyName}. Select {info.CaptureFriendlyName} as the microphone in other apps.");
        return entry;
    }

    private NodeEntry AddEffect(string typeId, double x, double y)
    {
        var descriptor = AudioProcessorCatalog.Find(typeId) ??
                         throw new InvalidOperationException($"Unknown effect '{typeId}'.");
        var modifier = new ModifierNode(descriptor.DisplayName);
        var processor = descriptor.Create();
        if (processor is WaveformProcessor waveform)
        {
            waveform.SetTapId(WaveformProcessor.NextTapId());
        }
        else if (processor is TranscriptionProcessor transcriber)
        {
            transcriber.SetTapId(TranscriptionProcessor.NextTapId());
        }

        modifier.Add(processor);
        var entry = RegisterModifier(modifier, x, y, transcriberModel: _settings.TranscriberModel);
        SetStatus($"Added effect: {descriptor.DisplayName}");
        return entry;
    }

    private NodeEntry AddChain(string name, double x, double y)
    {
        var chain = SubgraphLibrary.Chains.FirstOrDefault(item =>
                        string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) ??
                    throw new InvalidOperationException($"Chain '{name}' is no longer in the library.");
        var modifier = new ModifierNode(chain.Name);
        modifier.Add(chain.CreateProcessor());
        var entry = RegisterModifier(modifier, x, y, flavor: "chain", subtitle: chain.Description);
        SetStatus($"Added chain: {chain.Name}");
        return entry;
    }

    private AsioDriverInfo FindAsioDriver(string id)
    {
        return _asioDrivers.FirstOrDefault(driver => string.Equals(driver.Id, id, StringComparison.OrdinalIgnoreCase)) ??
               AsioCatalog.Discover().FirstOrDefault(driver => string.Equals(driver.Id, id, StringComparison.OrdinalIgnoreCase)) ??
               throw new InvalidOperationException($"ASIO driver '{AsioCatalog.NameFromId(id)}' is not available.");
    }

    private NodeEntry? FindOutputByDevice(string id)
    {
        return _nodes.Values.FirstOrDefault(entry => entry.Output != null &&
            !entry.Output.IsFileRecorder && string.Equals(entry.Output.DeviceId, id, StringComparison.OrdinalIgnoreCase));
    }

    private NodeEntry RegisterSource(SourceNode source, double x, double y, string? id = null)
    {
        _graph.AddSource(source);
        var entry = new NodeEntry
        {
            Id = id ?? source.Id,
            Kind = "source",
            Flavor = source.Kind switch
            {
                SourceKind.Application => "app",
                SourceKind.Device => "mic",
                SourceKind.Asio => "asio",
                _ => "system",
            },
            Title = source.Title,
            Subtitle = source.Kind switch
            {
                SourceKind.Application => "Application",
                SourceKind.Device => "Input device",
                SourceKind.Asio => "ASIO input",
                _ => "System audio",
            },
            Accent = source.Kind == SourceKind.Application ? "#5B8DEF" : "#B18CFF",
            X = x,
            Y = y,
            Source = source,
        };
        _nodes.Add(entry.Id, entry);
        source.Faulted += (node, reason) => _window.Dispatcher.BeginInvoke(new Action(() =>
            OnNodeFault(entry.Id, $"Capture '{node.Title}' stopped: {reason}")));
        return entry;
    }

    private NodeEntry RegisterOutput(OutputNode output, double x, double y, string? micHint = null, string? id = null)
    {
        _graph.Outputs.Add(output);
        string flavor = output.IsFileRecorder ? "recorder" : micHint != null ? "virtual" : AsioCatalog.IsAsioId(output.DeviceId) ? "asio" : "output";
        var entry = new NodeEntry
        {
            Id = id ?? output.Id,
            Kind = "output",
            Flavor = flavor,
            Title = output.Title,
            Subtitle = output.IsFileRecorder ? "File output" : micHint != null ? "Virtual output" : AsioCatalog.IsAsioId(output.DeviceId) ? "ASIO output" : "Output device",
            Accent = output.IsFileRecorder ? "#FF8B72" : micHint != null ? "#E5AD51" : "#43E39A",
            X = x,
            Y = y,
            MicHint = micHint,
            Output = output,
        };
        _nodes.Add(entry.Id, entry);
        output.Faulted += (node, reason) => _window.Dispatcher.BeginInvoke(new Action(() =>
            OnNodeFault(entry.Id, $"Output '{node.Title}' stopped: {reason}")));
        return entry;
    }

    private NodeEntry RegisterModifier(
        ModifierNode modifier,
        double x,
        double y,
        string flavor = "effect",
        string? subtitle = null,
        string? id = null,
        string? transcriberModel = null)
    {
        _graph.AddModifier(modifier);
        foreach (var waveform in modifier.Processors.OfType<WaveformProcessor>())
        {
            WaveformProcessor.EnsureTap(waveform.TapId);
        }

        bool hasTranscriber = modifier.Processors.OfType<TranscriptionProcessor>().Any();
        string? resolvedModel = hasTranscriber
            ? TranscriptionModels.Resolve(transcriberModel ?? _settings.TranscriberModel)
            : null;

        foreach (var transcriber in modifier.Processors.OfType<TranscriptionProcessor>())
        {
            TranscriptionProcessor.EnsureTap(transcriber.TapId);
            if (!string.IsNullOrEmpty(resolvedModel))
            {
                TranscriptionRegistry.Ensure(transcriber.TapId, resolvedModel);
            }
        }

        var entry = new NodeEntry
        {
            Id = id ?? modifier.Id,
            Kind = "modifier",
            Flavor = flavor,
            Title = modifier.Title,
            Subtitle = subtitle ?? "Processing",
            Accent = "#E5AD51",
            X = x,
            Y = y,
            TranscriberModel = resolvedModel,
            Modifier = modifier,
        };
        _nodes.Add(entry.Id, entry);
        return entry;
    }

    private void OnNodeFault(string id, string reason)
    {
        if (!_nodes.TryGetValue(id, out var entry) || _disposed)
        {
            return;
        }

        entry.Subtitle = entry.Kind == "source" ? "Capture stopped" : "Disconnected";
        ShowError(reason);
        PublishGraph();
        if (_running && !_graph.Outputs.Any(output => output.IsRunning))
        {
            StopRouting();
        }
    }

    private NodeDto CreateNodeDto(NodeEntry entry)
    {
        var dto = new NodeDto
        {
            Id = entry.Id,
            Kind = entry.Kind,
            Flavor = entry.Flavor,
            Title = entry.Title,
            Subtitle = entry.Subtitle,
            Accent = entry.Accent,
            X = entry.X,
            Y = entry.Y,
            MicHint = entry.MicHint,
        };

        if (entry.Source != null)
        {
            dto.Volume = entry.Source.Gain;
            dto.Muted = entry.Source.Muted;
        }
        else if (entry.Output != null)
        {
            dto.Volume = entry.Output.Volume;
            dto.Muted = entry.Output.Muted;
            dto.IsRecorder = entry.Output.IsFileRecorder;
            dto.RecordingPath = entry.Output.RecordingPath;
            dto.RecordingFormat = entry.Output.RecordingFormat.ToString();
        }
        else if (entry.Modifier != null)
        {
            dto.Enabled = entry.Modifier.Enabled;
            dto.IsWaveform = entry.Modifier.Processors.OfType<WaveformProcessor>().Any();
            dto.IsTranscriber = entry.Modifier.Processors.OfType<TranscriptionProcessor>().Any();
            dto.TranscriberModel = entry.TranscriberModel;
            var chain = entry.Modifier.Processors.OfType<ChainProcessor>().FirstOrDefault();
            if (chain != null)
            {
                dto.ChainName = chain.DisplayName;
                dto.ChainElements = chain.Processors.Select(processor => processor.DisplayName).ToList();
            }

            for (int processorIndex = 0; processorIndex < entry.Modifier.Processors.Count; processorIndex++)
            {
                if (entry.Modifier.Processors[processorIndex] is not IParameterizedProcessor parameterized)
                {
                    continue;
                }

                for (int parameterIndex = 0; parameterIndex < parameterized.Parameters.Count; parameterIndex++)
                {
                    var parameter = parameterized.Parameters[parameterIndex];
                    if (parameter.Name is WaveformProcessor.TapParameterName or TranscriptionProcessor.TapParameterName) continue;
                    dto.Parameters.Add(new ParameterDto
                    {
                        ProcessorIndex = processorIndex,
                        ParameterIndex = parameterIndex,
                        Name = parameter.Name,
                        Min = parameter.Min,
                        Max = parameter.Max,
                        Value = parameter.Get(),
                        Unit = parameter.Unit,
                        Logarithmic = parameter.Logarithmic,
                        IsToggle = parameter.IsToggle,
                        IsInteger = parameter.IsInteger,
                        IsChoice = parameter.IsChoice,
                        Options = parameter.Options,
                    });
                }
            }
        }

        return dto;
    }

    private void MoveNode(string id, double x, double y)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        double oldX = entry.X;
        double oldY = entry.Y;
        entry.X = Math.Max(0, x);
        entry.Y = Math.Max(0, y);
        double newX = entry.X;
        double newY = entry.Y;
        PushAction("Move node",
            undo: () => SetNodePosition(id, oldX, oldY, publish: true),
            redo: () => SetNodePosition(id, newX, newY, publish: true),
            coalesceKey: $"move:{id}");
    }

    private void SetNodePosition(string id, double x, double y, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        entry.X = Math.Max(0, x);
        entry.Y = Math.Max(0, y);
        if (publish) PublishGraph();
    }

    private void SetNodeGain(string id, float value)
    {
        value = Math.Clamp(value, 0, 1.5f);
        if (!_nodes.TryGetValue(id, out var entry)) return;
        float old = entry.Source?.Gain ?? entry.Output?.Volume ?? 1;
        if (entry.Source != null) entry.Source.Gain = value;
        if (entry.Output != null) entry.Output.Volume = value;
        PushAction("Change volume",
            undo: () => ApplyNodeGain(id, old, publish: true),
            redo: () => ApplyNodeGain(id, value, publish: true),
            coalesceKey: $"gain:{id}");
    }

    private void ApplyNodeGain(string id, float value, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        if (entry.Source != null) entry.Source.Gain = value;
        if (entry.Output != null) entry.Output.Volume = value;
        if (publish) PublishGraph();
    }

    private void SetNodeMute(string id, bool muted)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        bool old = entry.Source?.Muted ?? entry.Output?.Muted ?? false;
        ApplyNodeMute(id, muted, publish: true);
        PushAction("Mute node",
            undo: () => ApplyNodeMute(id, old, publish: true),
            redo: () => ApplyNodeMute(id, muted, publish: true));
    }

    private void ApplyNodeMute(string id, bool muted, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        if (entry.Source != null) entry.Source.Muted = muted;
        if (entry.Output != null) entry.Output.Muted = muted;
        if (publish) PublishGraph();
    }

    private void SetParameter(BridgeMessage message)
    {
        string id = String(message, "id");
        int processorIndex = (int)Number(message, "processor");
        int parameterIndex = (int)Number(message, "parameter");
        float value = (float)Number(message, "value");
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null ||
            processorIndex < 0 || processorIndex >= entry.Modifier.Processors.Count ||
            entry.Modifier.Processors[processorIndex] is not IParameterizedProcessor parameterized ||
            parameterIndex < 0 || parameterIndex >= parameterized.Parameters.Count)
        {
            return;
        }

        var parameter = parameterized.Parameters[parameterIndex];
        float old = parameter.Get();
        parameter.Set(value);
        string key = $"param:{id}:{processorIndex}:{parameterIndex}";
        PushAction($"Change {parameter.Name}",
            undo: () => ApplyParameter(id, processorIndex, parameterIndex, old, publish: true),
            redo: () => ApplyParameter(id, processorIndex, parameterIndex, value, publish: true),
            coalesceKey: key);
    }

    private void ApplyParameter(string id, int processorIndex, int parameterIndex, float value, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null ||
            processorIndex < 0 || processorIndex >= entry.Modifier.Processors.Count ||
            entry.Modifier.Processors[processorIndex] is not IParameterizedProcessor parameterized ||
            parameterIndex < 0 || parameterIndex >= parameterized.Parameters.Count)
        {
            return;
        }

        parameterized.Parameters[parameterIndex].Set(value);
        if (publish) PublishGraph();
    }

    private void SetEffectEnabled(string id, bool enabled)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        bool old = entry.Modifier.Enabled;
        ApplyEffectEnabled(id, enabled, publish: true);
        PushAction(enabled ? "Enable effect" : "Bypass effect",
            undo: () => ApplyEffectEnabled(id, old, publish: true),
            redo: () => ApplyEffectEnabled(id, enabled, publish: true));
        SetStatus($"{entry.Title}: {(enabled ? "enabled" : "bypassed")}.");
    }

    private void ApplyEffectEnabled(string id, bool enabled, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        entry.Modifier.Enabled = enabled;
        foreach (var output in _graph.Outputs)
        {
            _graph.RebuildRouteProcessors(output);
        }

        if (publish) PublishGraph();
    }

    private void RenameNode(string id, string title)
    {
        if (!_nodes.TryGetValue(id, out var entry) || string.IsNullOrWhiteSpace(title)) return;
        string old = entry.Title;
        title = title.Trim();
        title = title.Length > 80 ? title[..80] : title;
        ApplyRename(id, title, publish: true);
        PushAction("Rename node",
            undo: () => ApplyRename(id, old, publish: true),
            redo: () => ApplyRename(id, title, publish: true));
        SetStatus($"Renamed node to {title}.");
    }

    private void ApplyRename(string id, string title, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        entry.Title = title;
        if (entry.Source != null) entry.Source.Title = title;
        if (entry.Output != null) entry.Output.Title = title;
        if (entry.Modifier != null) entry.Modifier.Title = title;
        if (publish) PublishGraph();
    }

    private void ConnectNodes(string fromId, string toId)
    {
        if (!_nodes.TryGetValue(fromId, out var from) || !_nodes.TryGetValue(toId, out var to) || fromId == toId)
        {
            SetStatus("Choose a source or modifier OUT and a modifier or output IN.", isError: true);
            return;
        }

        if (_links.Any(link => link.From == fromId && link.To == toId))
        {
            SetStatus("Those nodes are already connected.", isError: true);
            return;
        }

        var link = ConnectEntries(from, to, publish: false);
        PushConnect(link);
        SetStatus($"Connected {from.Title} to {to.Title}.");
        PublishGraph();
    }

    private LinkEntry ConnectEntries(NodeEntry from, NodeEntry to, float gain = 1, bool publish = false, string? linkId = null)
    {
        string kind;
        if (from.Source != null && to.Output != null)
        {
            kind = "direct";
            _graph.Connect(from.Source, to.Output, gain);
        }
        else if (from.Source != null && to.Modifier != null)
        {
            kind = "source-modifier";
            _graph.ConnectSourceToModifier(from.Source, to.Modifier);
        }
        else if (from.Modifier != null && to.Output != null)
        {
            kind = "modifier-output";
            _graph.ConnectModifierToOutput(from.Modifier, to.Output);
        }
        else if (from.Modifier != null && to.Modifier != null)
        {
            kind = "modifier-modifier";
            _graph.ConnectModifierToModifier(from.Modifier, to.Modifier);
        }
        else
        {
            throw new InvalidOperationException($"Cannot connect {from.Title} to {to.Title}.");
        }

        var link = new LinkEntry
        {
            Id = linkId ?? Guid.NewGuid().ToString("N"),
            From = from.Id,
            To = to.Id,
            Kind = kind,
            Gain = gain,
        };
        _links.Add(link);
        if (publish) PublishGraph();
        return link;
    }

    private void DisconnectLink(string id)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        var captured = CloneLink(link);
        RemoveLinkInternal(link);
        PushDisconnect(captured);
        SetStatus("Cable disconnected.");
        PublishGraph();
    }

    private void RemoveLinkInternal(LinkEntry link)
    {
        var current = _links.FirstOrDefault(item => item.Id == link.Id);
        if (current == null)
        {
            return;
        }

        if (_nodes.TryGetValue(current.From, out var from) && _nodes.TryGetValue(current.To, out var to))
        {
            switch (current.Kind)
            {
                case "direct" when from.Source != null && to.Output != null:
                    _graph.Disconnect(from.Source, to.Output);
                    break;
                case "source-modifier" when from.Source != null && to.Modifier != null:
                    _graph.DisconnectSourceFromModifier(from.Source, to.Modifier);
                    break;
                case "modifier-output" when from.Modifier != null && to.Output != null:
                    _graph.DisconnectModifierFromOutput(from.Modifier, to.Output);
                    break;
                case "modifier-modifier" when from.Modifier != null && to.Modifier != null:
                    _graph.DisconnectModifierFromModifier(from.Modifier, to.Modifier);
                    break;
            }
        }

        _links.Remove(current);
    }

    private void DisconnectNode(string id)
    {
        var links = _links.Where(link => link.From == id || link.To == id).Select(CloneLink).ToList();
        if (links.Count == 0) return;

        foreach (var link in _links.Where(link => link.From == id || link.To == id).ToList())
        {
            RemoveLinkInternal(link);
        }

        PushAction("Disconnect routes",
            undo: () =>
            {
                RestoreLinks(links);
                PublishGraph();
            },
            redo: () =>
            {
                foreach (var link in links) RemoveLinkInternal(link);
                PublishGraph();
            });

        SetStatus("Node routes disconnected.");
        PublishGraph();
    }

    private void RemoveNode(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        var links = _links.Where(link => link.From == id || link.To == id).Select(CloneLink).ToList();
        RemoveEntryInternal(id);
        PushRemoveNode(entry, links);
        SetStatus($"Removed {entry.Title}.");
        PublishGraph();
    }

    private void RemoveEntryInternal(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        foreach (var link in _links.Where(link => link.From == id || link.To == id).ToList())
        {
            RemoveLinkInternal(link);
        }

        if (entry.Source != null) _graph.RemoveSource(entry.Source);
        if (entry.Output != null) _graph.RemoveOutput(entry.Output);
        if (entry.Modifier != null)
        {
            foreach (var transcriber in entry.Modifier.Processors.OfType<TranscriptionProcessor>())
            {
                TranscriptionRegistry.Stop(transcriber.TapId);
            }

            _graph.RemoveModifier(entry.Modifier);
        }

        _nodes.Remove(id);
        _nodeClipHoldUntil.Remove(id);
    }

    private void ClearGraph()
    {
        _graph.AutoRun = false;
        TranscriptionRegistry.StopAll();
        _graph.Dispose();
        _graph = new AudioGraph { AutoRun = false };
        _nodes.Clear();
        _links.Clear();
        _running = false;
        PublishTelemetry();
    }

    private void StartRouting()
    {
        _graph.AutoRun = true;
        int started = 0;
        int recordings = 0;
        int unarmed = 0;
        foreach (var entry in _nodes.Values)
        {
            try
            {
                if (entry.Source is { IsRunning: false } source)
                {
                    source.Start();
                    started++;
                }
                else if (entry.Output is { IsRunning: false } output)
                {
                    output.Start();
                    if (output.IsRecording) recordings++;
                    if (output.IsFileRecorder && !output.HasRecordingPath) unarmed++;
                }
            }
            catch (Exception ex)
            {
                ShowError($"Could not start '{entry.Title}': {ex.Message}", ex);
            }
        }

        _running = _graph.Outputs.Any(output => output.IsRunning);
        string recordingNote = recordings > 0 ? $", {recordings} recording(s)" : string.Empty;
        string unarmedNote = unarmed > 0 ? $"; {unarmed} recorder(s) need a file" : string.Empty;
        SetStatus($"Routing started · {started} source(s), {_graph.Outputs.Count} output(s){recordingNote}{unarmedNote}.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void StopRouting()
    {
        var recorders = _nodes.Values.Select(entry => entry.Output)
            .Where(output => output?.IsRecording == true).Cast<OutputNode>().ToList();
        _graph.AutoRun = false;
        foreach (var source in _graph.Sources) source.Stop();
        foreach (var output in _graph.Outputs) output.Stop();

        int saved = recorders.Count(output => string.IsNullOrEmpty(output.RecordingError));
        int failed = recorders.Count - saved;
        _running = false;
        SetStatus(recorders.Count == 0
            ? "Routing stopped."
            : $"Routing stopped · {saved} recording(s) saved{(failed > 0 ? $", {failed} failed" : string.Empty)}.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetMicrophoneMute(bool muted)
    {
        _micMuted = muted;
        int affected = 0;
        foreach (var source in _graph.Sources.Where(source => source.Kind == SourceKind.Device))
        {
            source.Muted = muted;
            affected++;
        }

        SetStatus(muted ? $"Microphones muted · {affected} input(s)." : "Microphones live again.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetRouteGain(string id, float gain)
    {
        var link = _links.FirstOrDefault(item => item.Id == id && item.Kind == "direct");
        if (link == null || !_nodes.TryGetValue(link.From, out var from) ||
            !_nodes.TryGetValue(link.To, out var to) || from.Source == null || to.Output == null)
        {
            return;
        }

        gain = Math.Clamp(gain, 0, 2);
        float old = link.Gain;
        ApplyRouteGain(id, gain, publish: true);
        PushAction("Set route gain",
            undo: () => ApplyRouteGain(id, old, publish: true),
            redo: () => ApplyRouteGain(id, gain, publish: true));
        SetStatus($"Route gain set to {gain:P0}.");
    }

    private void ApplyRouteGain(string id, float gain, bool publish)
    {
        var link = _links.FirstOrDefault(item => item.Id == id && item.Kind == "direct");
        if (link == null || !_nodes.TryGetValue(link.From, out var from) ||
            !_nodes.TryGetValue(link.To, out var to) || from.Source == null || to.Output == null)
        {
            return;
        }

        link.Gain = gain;
        var route = to.Output.Inputs.FirstOrDefault(item => item.Source == from.Source);
        if (route != null) route.Gain = gain;
        if (publish) PublishGraph();
    }

    private void ChooseRecorderFile(string id, string formatName)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Output is not { IsFileRecorder: true } output) return;
        var format = AudioFileFormats.Get(AudioFileFormats.Parse(formatName));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Choose recording destination",
            Filter = format.Filter,
            FileName = output.HasRecordingPath ? output.RecordingPath : $"ruse-recording.{format.Extension}",
            DefaultExt = format.Extension,
            AddExtension = true,
        };
        if (dialog.ShowDialog(_window) != true) return;

        output.SetRecordingDestination(dialog.FileName, format.Format);
        entry.Title = output.Title;
        if (_graph.AutoRun && output.Inputs.Count > 0)
        {
            _graph.EnsureRunning(output);
        }

        SetStatus($"Recorder armed: {Path.GetFileName(dialog.FileName)}");
        PublishGraph();
    }

    private void StopRecorder(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Output is not { IsFileRecorder: true, IsRecording: true } output)
        {
            return;
        }

        output.Stop();
        _running = _graph.Outputs.Any(item => item.IsRunning);
        string result = string.IsNullOrEmpty(output.RecordingError)
            ? "Recording saved."
            : "Recording failed: " + output.RecordingError;
        SetStatus($"{output.Title}: {result}");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetRecorderFormat(string id, string formatName)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Output is not { IsFileRecorder: true } output) return;
        var format = AudioFileFormats.Get(AudioFileFormats.Parse(formatName));
        string path = output.HasRecordingPath
            ? Path.ChangeExtension(output.RecordingPath, format.Extension)
            : string.Empty;
        output.SetRecordingDestination(path, format.Format);
        entry.Title = string.IsNullOrEmpty(path) ? "File recorder" : output.Title;
        output.Title = entry.Title;
        PublishGraph();
    }

    private void DuplicateNode(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        if (entry.Output != null)
        {
            SetStatus("Outputs and recorders cannot be duplicated.", isError: true);
            return;
        }

        var copy = CaptureNode(entry) with
        {
            Id = Guid.NewGuid().ToString("N"),
            X = entry.X + 40,
            Y = entry.Y + 40,
            Title = entry.Title,
        };
        var restored = RestoreNodeSnapshot(copy);
        if (restored == null) return;
        var redoSnapshot = CaptureNode(restored);
        PushAction("Duplicate node",
            undo: () =>
            {
                RemoveEntryInternal(restored.Id);
                PublishGraph();
            },
            redo: () =>
            {
                RestoreNodeSnapshot(redoSnapshot);
                PublishGraph();
            });
        SetStatus($"Duplicated '{entry.Title}'.");
        PublishGraph();
    }

    private void MoveNodes(BridgeMessage message)
    {
        if (!message.Payload.TryGetProperty("positions", out var positions) ||
            positions.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var changes = new List<(string Id, double OldX, double OldY, double NewX, double NewY)>();
        foreach (var item in positions.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idValue) ||
                !item.TryGetProperty("x", out var xValue) ||
                !item.TryGetProperty("y", out var yValue) ||
                idValue.GetString() is not { } nodeId ||
                !_nodes.TryGetValue(nodeId, out var entry))
            {
                continue;
            }

            double x = Math.Max(0, xValue.GetDouble());
            double y = Math.Max(0, yValue.GetDouble());
            changes.Add((nodeId, entry.X, entry.Y, x, y));
            entry.X = x;
            entry.Y = y;
        }

        if (changes.Count == 0) return;
        PushAction("Tidy graph",
            undo: () =>
            {
                foreach (var change in changes) SetNodePosition(change.Id, change.OldX, change.OldY, publish: false);
                PublishGraph();
            },
            redo: () =>
            {
                foreach (var change in changes) SetNodePosition(change.Id, change.NewX, change.NewY, publish: false);
                PublishGraph();
            });
        PublishGraph();
    }

    private void BuildLiveGraph(uint processId)
    {
        _graph.AutoRun = false;
        var source = RegisterSource(new SourceNode("Live audio", SourceKind.Application, processId), 40, 140);
        var modifier = AddEffect(HighPassProcessor.Id, 400, 140);
        var output = _catalog.Outputs.FirstOrDefault() is { } first
            ? AddDeviceOutput(first.Id, 740, 140)
            : null;
        ConnectEntries(source, modifier);
        if (output != null)
        {
            ConnectEntries(modifier, output);
        }

        SetStatus("Live graph · carrying real audio.");
        StartRouting();
    }

    private void BuildDemoGraph()
    {
        _graph.AutoRun = false;
        var discord = RegisterSource(new SourceNode("Discord", SourceKind.Application, 1234), 40, 40);
        var game = RegisterSource(new SourceNode("Game (Counter-Strike)", SourceKind.Application, 5678), 40, 280);
        var mic = RegisterSource(new SourceNode("Microphone", SourceKind.Device, deviceId: _catalog.Inputs.FirstOrDefault()?.Id), 40, 560);
        var spotify = RegisterSource(new SourceNode("Spotify", SourceKind.Application, 91011), 40, 800);
        var highPass = AddEffect(HighPassProcessor.Id, 390, 250);
        var compressor = AddEffect("compressor", 680, 500);

        var headphones = _catalog.Outputs.FirstOrDefault() is { } first ? AddDeviceOutput(first.Id, 900, 70) : null;
        var virtualOutput = _catalog.Virtuals.FirstOrDefault() is { } virtualItem ? AddVirtualOutput(virtualItem.Id, 930, 560) : null;
        var recorder = RegisterOutput(OutputNode.CreateForFile(string.Empty, AudioFileFormat.Wav24), 930, 850);

        if (headphones != null)
        {
            ConnectEntries(discord, headphones);
            ConnectEntries(game, headphones);
            ConnectEntries(spotify, headphones);
        }

        ConnectEntries(mic, highPass);
        ConnectEntries(highPass, compressor);
        if (virtualOutput != null)
        {
            ConnectEntries(compressor, virtualOutput);
            ConnectEntries(discord, virtualOutput);
        }

        ConnectEntries(compressor, recorder);
        StartRouting();
    }
}
