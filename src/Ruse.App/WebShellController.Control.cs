using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private static readonly JsonSerializerOptions ControlJson = new(JsonSerializerDefaults.Web);
    private ControlServer? _controlServer;
    private bool _controlDirty;
    private readonly HashSet<(string NodeId, int ProcessorIndex, int ParameterIndex)> _controlParameterChanges = new();
    private readonly object _controlParameterLock = new();
    private long _lastControlGraphBroadcast;
    private bool _controlGraphPending;

    internal Dispatcher Dispatcher => _window.Dispatcher;

    internal async Task StartControlServerAsync()
    {
        if (!_settings.ControlApiEnabled)
        {
            ControlServer.DeleteDiscoveryFile();
            return;
        }

        if (_controlServer != null)
        {
            return;
        }

        try
        {
            var server = new ControlServer(this);
            await server.StartAsync(_settings.ControlApiPort);
            if (_disposed)
            {
                server.Dispose();
                return;
            }

            _controlServer = server;
            PublishSettings();
        }
        catch (Exception ex)
        {
            _controlServer = null;
            AppLog.Warning("The control API could not start: " + ex.Message);
        }
    }

    private async Task RestartControlServerAsync()
    {
        StopControlServer();
        if (_settings.ControlApiEnabled)
        {
            await StartControlServerAsync();
        }

        PublishSettings();
    }

    private void StopControlServer()
    {
        var server = _controlServer;
        _controlServer = null;
        if (server == null)
        {
            return;
        }

        try
        {
            server.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warning("The control API did not stop cleanly: " + ex.Message);
        }
    }

    private void FlushControlParameterChanges()
    {
        (string NodeId, int ProcessorIndex, int ParameterIndex)[] changes;
        lock (_controlParameterLock)
        {
            changes = _controlParameterChanges.Count == 0
                ? Array.Empty<(string, int, int)>()
                : _controlParameterChanges.ToArray();
            _controlParameterChanges.Clear();
        }

        for (int i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            PublishParameterChange(change.NodeId, change.ProcessorIndex, change.ParameterIndex);
        }

        if (_controlDirty)
        {
            _controlDirty = false;
            PublishGraph();
        }

        if (_controlGraphPending)
        {
            PublishControlGraphIfDue();
        }
    }

    internal void PublishControlGraphIfDue()
    {
        if (_controlServer == null || !_controlServer.HasClients)
        {
            _controlGraphPending = false;
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastControlGraphBroadcast < 250)
        {
            _controlGraphPending = true;
            return;
        }

        _controlGraphPending = false;
        _lastControlGraphBroadcast = now;
        _controlServer.PublishGraph(_graphVersion, _running, BuildControlNodes());
    }

    internal ControlStateDto BuildControlState() => new()
    {
        Running = _running,
        NodeCount = _nodes.Count,
        LinkCount = _links.Count,
        Version = AppVersionText,
        Status = _statusText,
    };

    internal List<ControlNodeDto> BuildControlNodes()
    {
        var nodes = new List<ControlNodeDto>();
        foreach (var entry in _nodes.Values.OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase))
        {
            var node = new ControlNodeDto
            {
                Id = entry.Id,
                ControlId = entry.ControlId,
                Kind = entry.Kind,
                Title = entry.Title,
                Flavor = entry.Flavor,
                Enabled = entry.Modifier?.Enabled ?? true,
            };

            if (entry.Source != null)
            {
                node.Gain = entry.Source.Gain;
                node.Muted = entry.Source.Muted;
                node.Solo = entry.Source.Solo;
            }
            else if (entry.Output != null)
            {
                node.Gain = entry.Output.Volume;
                node.Muted = entry.Output.Muted;
                node.Solo = entry.Output.Solo;
            }
            else if (entry.Modifier != null)
            {
                for (int index = 0; index < entry.Modifier.Processors.Count; index++)
                {
                    var processor = entry.Modifier.Processors[index];
                    if (processor is not IParameterizedProcessor parameterized)
                    {
                        continue;
                    }

                    var parameters = new List<ControlParameterDto>();
                    for (int parameterIndex = 0; parameterIndex < parameterized.Parameters.Count; parameterIndex++)
                    {
                        var parameter = parameterized.Parameters[parameterIndex];
                        if (!IsControlVisibleParameter(processor, parameter))
                        {
                            continue;
                        }

                        float value = parameter.Get();
                        float range = parameter.Max - parameter.Min;
                        parameters.Add(new ControlParameterDto
                        {
                            Index = parameterIndex,
                            Name = parameter.Name,
                            Min = parameter.Min,
                            Max = parameter.Max,
                            Value = value,
                            Normalized = range > 0 ? Math.Clamp((value - parameter.Min) / range, 0f, 1f) : 0f,
                            Unit = parameter.Unit,
                            IsToggle = parameter.IsToggle,
                            IsInteger = parameter.IsInteger,
                            IsChoice = parameter.IsChoice,
                            Options = parameter.Options,
                        });
                    }

                    if (parameters.Count == 0)
                    {
                        continue;
                    }

                    node.Processors.Add(new ControlProcessorDto
                    {
                        Index = index,
                        TypeId = processor.TypeId,
                        Name = processor.DisplayName,
                        Parameters = parameters,
                    });
                }
            }

            nodes.Add(node);
        }

        return nodes;
    }

    internal ControlTelemetryDto BuildControlTelemetry()
    {
        var telemetry = new ControlTelemetryDto { Running = _running };
        foreach (var entry in _nodes.Values)
        {
            if (entry.Source != null)
            {
                telemetry.Nodes.Add(new ControlTelemetryNodeDto
                {
                    Id = entry.Id,
                    Title = entry.Title,
                    Level = entry.Source.MeterLevel,
                    LevelLeft = entry.Source.MeterLevelLeft,
                    LevelRight = entry.Source.MeterLevelRight,
                    Clipping = entry.Source.Clipping,
                    Muted = entry.Source.Muted,
                });
            }
            else if (entry.Output != null)
            {
                telemetry.Nodes.Add(new ControlTelemetryNodeDto
                {
                    Id = entry.Id,
                    Title = entry.Title,
                    Level = entry.Output.MeterLevel,
                    LevelLeft = entry.Output.MeterLevelLeft,
                    LevelRight = entry.Output.MeterLevelRight,
                    Clipping = entry.Output.Clipping,
                    Muted = entry.Output.Muted,
                });
            }
        }

        return telemetry;
    }

    internal bool TrySetControlParameter(
        string nodeId, int processorIndex, int parameterIndex,
        float? value, float? normalized, out string error)
    {
        var entry = ResolveControlNode(nodeId);
        if (entry?.Modifier == null)
        {
            error = "Node not found or it has no effect parameters.";
            return false;
        }

        if (processorIndex < 0 || processorIndex >= entry.Modifier.Processors.Count ||
            entry.Modifier.Processors[processorIndex] is not IParameterizedProcessor parameterized ||
            parameterIndex < 0 || parameterIndex >= parameterized.Parameters.Count)
        {
            error = "Effect or parameter index is out of range.";
            return false;
        }

        var parameter = parameterized.Parameters[parameterIndex];
        if (!IsControlVisibleParameter(entry.Modifier.Processors[processorIndex], parameter))
        {
            error = "That parameter is internal and cannot be controlled.";
            return false;
        }

        float target;
        if (value.HasValue)
        {
            target = value.Value;
        }
        else if (normalized.HasValue)
        {
            target = parameter.Min + (parameter.Max - parameter.Min) *
                Math.Clamp(normalized.Value, 0f, 1f);
        }
        else
        {
            error = "Provide value or normalized.";
            return false;
        }

        target = Math.Clamp(target, parameter.Min, parameter.Max);
        if (parameter.IsInteger)
        {
            target = MathF.Round(target);
        }

        parameter.Set(target);
        lock (_controlParameterLock)
        {
            _controlParameterChanges.Add((entry.Id, processorIndex, parameterIndex));
        }

        error = "";
        return true;
    }

    internal bool TryPatchControlNode(string nodeId, ControlNodePatchRequest patch, out string error)
    {
        var entry = ResolveControlNode(nodeId);
        if (entry == null)
        {
            error = "Node not found.";
            return false;
        }

        bool touchesGain = patch.Gain.HasValue;
        bool touchesMute = patch.Muted.HasValue;
        bool touchesSolo = patch.Solo.HasValue;
        bool touchesEnabled = patch.Enabled.HasValue;
        bool touchesControlId = patch.ControlId != null;
        if (!touchesGain && !touchesMute && !touchesSolo && !touchesEnabled && !touchesControlId)
        {
            error = "No changes were provided.";
            return false;
        }

        string? controlId = null;
        if (touchesControlId)
        {
            controlId = patch.ControlId!.Trim();
            if (!IsValidControlId(controlId))
            {
                error = "controlId must start with a letter or digit and use only letters, digits, '-', '_' or '.'.";
                return false;
            }

            if (_nodes.TryGetValue(controlId, out var idOwner) && !ReferenceEquals(idOwner, entry))
            {
                error = "That controlId conflicts with a node ID.";
                return false;
            }

            if (_nodes.Values.Any(other => !ReferenceEquals(other, entry) &&
                string.Equals(other.ControlId, controlId, StringComparison.OrdinalIgnoreCase)))
            {
                error = "That controlId is already in use.";
                return false;
            }
        }

        if ((touchesGain || touchesMute || touchesSolo) && entry.Source == null && entry.Output == null)
        {
            error = "Gain, mute and solo apply to sources and outputs.";
            return false;
        }

        if (touchesEnabled && entry.Modifier == null)
        {
            error = "Enabled applies to effects.";
            return false;
        }

        if (patch.Gain is float gain)
        {
            gain = Math.Clamp(gain, 0f, 2f);
            if (entry.Source != null)
            {
                entry.Source.Gain = gain;
            }
            else
            {
                entry.Output!.Volume = gain;
            }
        }

        if (patch.Muted is bool muted)
        {
            ApplyNodeMute(entry.Id, muted, publish: false);
        }

        if (patch.Solo is bool solo)
        {
            if (entry.Source != null)
            {
                entry.Source.Solo = solo;
            }
            else
            {
                entry.Output!.Solo = solo;
            }
        }

        if (patch.Enabled is bool enabled)
        {
            entry.Modifier!.Enabled = enabled;
            _graph.RefreshModifiers();
        }

        if (controlId != null)
        {
            entry.ControlId = controlId;
            _controlDirty = true;
        }
        else
        {
            PublishNodeState(entry.Id);
        }

        error = "";
        return true;
    }

    internal bool TrySetControlTransport(bool running, out string error)
    {
        if (running)
        {
            StartRouting();
        }
        else
        {
            StopRouting();
        }

        error = "";
        return true;
    }

    internal string HandleControlWebSocketMessage(string json)
    {
        ControlWsRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ControlWsRequest>(json, ControlJson);
        }
        catch (JsonException)
        {
            return ControlError(null, "Invalid JSON.");
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Type))
        {
            return ControlError(null, "Missing message type.");
        }

        switch (request.Type)
        {
            case "ping":
                return ControlAck(request.Id);
            case "get-state":
                return JsonSerializer.Serialize(new
                {
                    type = "state",
                    id = request.Id,
                    state = BuildControlState(),
                }, ControlJson);
            case "get-nodes":
                return JsonSerializer.Serialize(new
                {
                    type = "nodes",
                    id = request.Id,
                    nodes = BuildControlNodes(),
                }, ControlJson);
            case "set-parameter":
                if (string.IsNullOrWhiteSpace(request.NodeId) ||
                    request.Processor == null || request.Parameter == null)
                {
                    return ControlError(request.Id, "nodeId, processor and parameter are required.");
                }

                return TrySetControlParameter(
                    request.NodeId,
                    request.Processor.Value,
                    request.Parameter.Value,
                    request.Value.HasValue ? (float)request.Value.Value : null,
                    request.Normalized.HasValue ? (float)request.Normalized.Value : null,
                    out string parameterError)
                    ? ControlAck(request.Id)
                    : ControlError(request.Id, parameterError);
            case "set-node":
                if (string.IsNullOrWhiteSpace(request.NodeId))
                {
                    return ControlError(request.Id, "nodeId is required.");
                }

                var patch = new ControlNodePatchRequest
                {
                    Gain = request.Gain.HasValue ? (float)request.Gain.Value : null,
                    Muted = request.Muted,
                    Solo = request.Solo,
                    Enabled = request.Enabled,
                    ControlId = request.ControlId,
                };
                return TryPatchControlNode(request.NodeId, patch, out string nodeError)
                    ? ControlAck(request.Id)
                    : ControlError(request.Id, nodeError);
            case "transport":
                if (request.Running == null)
                {
                    return ControlError(request.Id, "running is required.");
                }

                return TrySetControlTransport(request.Running.Value, out string transportError)
                    ? ControlAck(request.Id)
                    : ControlError(request.Id, transportError);
            default:
                return ControlError(request.Id, $"Unknown message type '{request.Type}'.");
        }
    }

    private NodeEntry? ResolveControlNode(string idOrTitle)
    {
        if (string.IsNullOrWhiteSpace(idOrTitle))
        {
            return null;
        }

        if (_nodes.TryGetValue(idOrTitle, out var entry))
        {
            return entry;
        }

        var byControlId = _nodes.Values
            .Where(candidate => string.Equals(candidate.ControlId, idOrTitle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byControlId.Count == 1)
        {
            return byControlId[0];
        }

        var byTitle = _nodes.Values
            .Where(candidate => string.Equals(candidate.Title, idOrTitle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return byTitle.Count == 1 ? byTitle[0] : null;
    }

    private static bool IsControlVisibleParameter(IAudioProcessor processor, AudioParameter parameter)
    {
        if (parameter.Name is SpectrumProcessor.TapParameterName or
            SpectrogramProcessor.TapParameterName or
            TranscriptionProcessor.TapParameterName or
            DbMeterProcessor.TapParameterName)
        {
            return false;
        }

        return processor is not DbMeterProcessor || parameter.Name != "Orientation";
    }

    internal string MakeUniqueControlId(NodeEntry entry, string? preferred)
    {
        string baseId = !string.IsNullOrWhiteSpace(preferred) && IsValidControlId(preferred.Trim())
            ? preferred.Trim()
            : SlugifyControlId(entry.Title);
        string candidate = baseId;
        int suffix = 2;
        while (IsControlIdTaken(candidate, entry))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

    private bool IsControlIdTaken(string candidate, NodeEntry owner)
    {
        if (_nodes.TryGetValue(candidate, out var idOwner) && !ReferenceEquals(idOwner, owner))
        {
            return true;
        }

        foreach (var other in _nodes.Values)
        {
            if (!ReferenceEquals(other, owner) &&
                string.Equals(other.ControlId, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string SlugifyControlId(string title)
    {
        var builder = new StringBuilder(title.Length);
        bool pendingDash = false;
        foreach (char raw in title)
        {
            char c = char.ToLowerInvariant(raw);
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                builder.Append(c);
                pendingDash = false;
            }
            else if (builder.Length > 0 && !pendingDash)
            {
                builder.Append('-');
                pendingDash = true;
            }
        }

        string slug = builder.ToString().Trim('-');
        if (slug.Length > 48)
        {
            slug = slug[..48].Trim('-');
        }

        return string.IsNullOrEmpty(slug) ? "node" : slug;
    }

    private static bool IsValidControlId(string id)
    {
        if (id.Length is 0 or > 64 || !IsAsciiLetterOrDigit(id[0]))
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    private static string ControlAck(int? id) =>
        JsonSerializer.Serialize(new { type = "ack", id }, ControlJson);

    private static string ControlError(int? id, string message) =>
        JsonSerializer.Serialize(new { type = "error", id, message }, ControlJson);
}
