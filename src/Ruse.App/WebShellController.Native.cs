using NAudio.CoreAudioApi;
using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private void SetLinkNative(string id, bool enabled)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null)
        {
            return;
        }

        if (!enabled)
        {
            link.NativePreference = false;
            DisableNativeChainForLink(link);
            return;
        }

        if (link.Native)
        {
            return;
        }

        if (link.Kind != "direct" ||
            !_nodes.TryGetValue(link.From, out var from) || from.Source == null ||
            !_nodes.TryGetValue(link.To, out var to) || to.Output == null)
        {
            SetStatus("Native passthrough needs a direct source-to-device cable.", isError: true);
            return;
        }

        if (enabled && _links.Any(other => other.Id != link.Id && other.To == link.To))
        {
            SetStatus("Native passthrough needs to be the only route to that output.", isError: true);
            return;
        }

        if (to.Output.IsRecording)
        {
            SetStatus("Stop recording before changing native passthrough.", isError: true);
            return;
        }

        if (enabled && (!link.Enabled || Math.Abs(link.Gain - 1f) > 1e-4f))
        {
            SetStatus("Set the cable to 100% and enabled before enabling native passthrough.", isError: true);
            return;
        }

        string? error;
        if (enabled)
        {
            error = _graph.SetNativePassthrough(to.Output, from.Source, true);
        }
        else
        {
            error = _graph.SetNativePassthrough(to.Output, from.Source, false);
            if (error == null)
            {
                link.Native = false;
            }
        }

        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        if (enabled)
        {
            link.Native = true;
            link.NativePreference = true;
            if (to.Output != null)
            {
                _nativeFallbackSources.Remove(to.Id);
            }
        }

        PushAction(enabled ? "Enable native passthrough" : "Disable native passthrough",
            undo: () => ApplyLinkNative(id, !enabled, publish: true),
            redo: () => ApplyLinkNative(id, enabled, publish: true),
            structural: false);
        PublishLinkState(id);
        PublishTelemetry(force: true);
        SetStatus(enabled
            ? $"Native passthrough: {from.Title} → {to.Title}."
            : $"Normal routing restored for {to.Title}.");
    }

    private bool TryBuildNativeChain(
        OutputNode output,
        out SourceNode? source,
        out IAudioProcessor[] processors,
        out List<LinkEntry> links,
        out string reason)
    {
        source = null;
        processors = Array.Empty<IAudioProcessor>();
        links = new List<LinkEntry>();
        reason = "";

        var outputEntry = _nodes.Values.FirstOrDefault(entry => ReferenceEquals(entry.Output, output));
        if (outputEntry == null)
        {
            reason = "The output is no longer in the graph.";
            return false;
        }

        if (_links.Count(link => link.To == outputEntry.Id) != 1)
        {
            reason = "Native passthrough needs a single route to the output.";
            return false;
        }

        var modifiers = new List<ModifierNode>();
        string currentId = outputEntry.Id;
        for (int depth = 0; depth < 64; depth++)
        {
            var incoming = _links.Where(link => link.To == currentId && link.Native).ToList();
            if (incoming.Count != 1)
            {
                reason = "Every cable in a native chain must be native.";
                return false;
            }

            var link = incoming[0];
            if (_links.Count(candidate => candidate.To == currentId) != 1)
            {
                reason = "A native node cannot have another incoming route.";
                return false;
            }

            links.Add(link);
            if (!_nodes.TryGetValue(link.From, out var from))
            {
                reason = "A native chain node is missing.";
                return false;
            }

            if (from.Source != null)
            {
                source = from.Source;
                break;
            }

            if (from.Modifier == null)
            {
                reason = "A native chain must start at one source.";
                return false;
            }

            var modifier = from.Modifier;
            if (modifier.Parallel ||
                modifier.SidechainInputs.Count > 0 ||
                modifier.SidechainModifiers.Count > 0 ||
                modifier.Next.Count > 1 ||
                modifier.Outputs.Any(item => !ReferenceEquals(item, output)))
            {
                reason = "Parallel, sidechain or shared-route nodes cannot run in native passthrough.";
                return false;
            }

            modifiers.Add(modifier);
            currentId = from.Id;
        }

        if (source == null)
        {
            reason = "Native passthrough needs a source.";
            return false;
        }

        var chain = new List<IAudioProcessor>();
        for (int i = modifiers.Count - 1; i >= 0; i--)
        {
            var modifier = modifiers[i];
            if (!modifier.Enabled)
            {
                continue;
            }

            foreach (var processor in modifier.Processors)
            {
                if (processor is not INativeProcessor)
                {
                    reason = $"'{processor.DisplayName}' does not support native-rate processing.";
                    return false;
                }

                chain.Add(processor);
            }
        }

        processors = chain.ToArray();
        return true;
    }

    private bool RefreshNativeChain(OutputNode output, bool quiet = false)
    {
        if (!TryBuildNativeChain(output, out var source, out var processors,
                out _, out string reason))
        {
            if (!quiet) SetStatus(reason, isError: true);
            return false;
        }

        string? error = _graph.SetNativePassthrough(output, source!, true, processors);
        if (error != null)
        {
            if (!quiet) SetStatus(error, isError: true);
            return false;
        }

        return true;
    }

    private bool TryFindNativeOutput(LinkEntry link, out OutputNode? output)
    {
        output = null;
        string currentId = link.To;
        for (int depth = 0; depth < 64; depth++)
        {
            if (!_nodes.TryGetValue(currentId, out var entry))
            {
                return false;
            }

            if (entry.Output != null)
            {
                output = entry.Output;
                return true;
            }

            var next = _links.FirstOrDefault(candidate =>
                candidate.From == currentId && candidate.Native);
            if (next == null)
            {
                return false;
            }

            currentId = next.To;
        }

        return false;
    }

    private bool TryFindOutputForLink(LinkEntry link, out OutputNode? output)
    {
        output = null;
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(link.To);
        while (pending.Count > 0)
        {
            string current = pending.Dequeue();
            if (!visited.Add(current) || !_nodes.TryGetValue(current, out var entry))
            {
                continue;
            }

            if (entry.Output != null)
            {
                output = entry.Output;
                return true;
            }

            foreach (var next in _links.Where(item => item.From == current && item.Kind != "sidechain"))
            {
                pending.Enqueue(next.To);
            }
        }

        return false;
    }

    private void ClearNativeFlagsIntoOutput(OutputNode output)
    {
        var outputEntry = _nodes.Values.FirstOrDefault(entry => ReferenceEquals(entry.Output, output));
        if (outputEntry == null) return;

        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(outputEntry.Id);
        while (pending.Count > 0)
        {
            string current = pending.Dequeue();
            if (!visited.Add(current)) continue;
            foreach (var link in _links.Where(item => item.To == current))
            {
                link.Native = false;
                pending.Enqueue(link.From);
            }
        }
    }

    private bool DisableNativeOnSharedOutput(OutputNode output)
    {
        if (output.NativeSource == null)
        {
            return false;
        }

        var entry = _nodes.Values.FirstOrDefault(item => ReferenceEquals(item.Output, output));
        if (entry == null || _links.Count(link => link.To == entry.Id) <= 1)
        {
            return false;
        }

        var nativeLink = _links.FirstOrDefault(link => link.To == entry.Id && link.Native);
        ClearNativeFlagsIntoOutput(output);
        string? error = _graph.SetNativePassthrough(output, output.NativeSource, false);
        if (error == null && nativeLink != null)
        {
            _nativeFallbackSources[entry.Id] = nativeLink.From;
        }

        PublishGraph();
        PublishTelemetry(force: true);
        string message = error ?? $"{entry.Title}: native passthrough disabled because another route was connected.";
        SetStatus(message, error != null);
        ShowToast(message, error != null);
        return error == null;
    }

    private void TryRestoreNativeFallback(OutputNode output)
    {
        var entry = _nodes.Values.FirstOrDefault(item => ReferenceEquals(item.Output, output));
        if (entry == null)
        {
            return;
        }

        if (!_nativeFallbackSources.TryGetValue(entry.Id, out string? sourceId))
        {
            return;
        }

        var links = _links.Where(link => link.To == entry.Id).ToList();
        if (links.Count == 0)
        {
            _nativeFallbackSources.Remove(entry.Id);
            return;
        }

        if (links.Count != 1 || links[0].From != sourceId || links[0].Kind != "direct" ||
            output.NativeSource != null)
        {
            return;
        }

        var link = links[0];
        link.Native = true;
        if (!RefreshNativeChain(output))
        {
            link.Native = false;
            return;
        }

        _nativeFallbackSources.Remove(entry.Id);
        PublishGraph();
        PublishTelemetry(force: true);
        string message = $"{entry.Title}: native passthrough restored.";
        SetStatus(message);
        ShowToast(message);
    }

    private bool IsNativeFallback(LinkEntry link) =>
        !link.Native &&
        _nativeFallbackSources.TryGetValue(link.To, out string? sourceId) &&
        string.Equals(sourceId, link.From, StringComparison.Ordinal);

    private void CancelNativeRestore(string linkId)
    {
        var link = _links.FirstOrDefault(item => item.Id == linkId);
        if (link == null ||
            !_nativeFallbackSources.TryGetValue(link.To, out string? sourceId) ||
            sourceId != link.From)
        {
            return;
        }

        _nativeFallbackSources.Remove(link.To);
        PublishGraph();
        SetStatus("Native passthrough restore cancelled.");
        ShowToast("Native passthrough restore cancelled.");
    }

    private void DisableNativeChainForLink(LinkEntry link)
    {
        bool found = TryFindOutputForLink(link, out var output);
        link.Native = false;
        if (found && output != null)
        {
            var outputEntry = _nodes.Values.FirstOrDefault(item => ReferenceEquals(item.Output, output));
            if (outputEntry != null)
            {
                _nativeFallbackSources.Remove(outputEntry.Id);
            }

            ClearNativeFlagsIntoOutput(output);
        }

        string? error = null;
        if (found && output?.NativeSource != null)
        {
            error = _graph.SetNativePassthrough(output, output.NativeSource, false);
        }

        PublishGraph();
        PublishTelemetry(force: true);
        SetStatus(error ?? "Native passthrough disabled.", error != null);
    }

    private void ApplyLinkNative(string id, bool enabled, bool publish)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null || link.Native == enabled)
        {
            return;
        }

        if (!enabled)
        {
            link.NativePreference = false;
            DisableNativeChainForLink(link);
            return;
        }

        if (!_nodes.TryGetValue(link.From, out var from) || from.Source == null ||
            !_nodes.TryGetValue(link.To, out var to) || to.Output == null)
        {
            return;
        }

        string? error = _graph.SetNativePassthrough(to.Output, from.Source, enabled);
        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        link.Native = enabled;
        link.NativePreference = enabled;
        if (publish)
        {
            PublishLinkState(id);
            PublishTelemetry(force: true);
        }
    }

    private string? NativeBlockedReason(LinkEntry link)
    {
        if (link.Native || link.Kind != "direct" || !_settings.PreferNativePassthrough)
        {
            return null;
        }

        if (link.NativePreference == false)
        {
            return "Native passthrough is disabled for this cable.";
        }

        if (!_nodes.TryGetValue(link.From, out var from) || from.Source == null ||
            !_nodes.TryGetValue(link.To, out var to) || to.Output == null)
        {
            return "Native passthrough needs a source and an output.";
        }

        if (!link.Enabled)
        {
            return "The cable is disabled.";
        }

        if (Math.Abs(link.Gain - 1f) > 1e-4f)
        {
            return "The cable is not at 100% gain.";
        }

        if (_links.Count(item => item.To == link.To) != 1)
        {
            return "Native passthrough needs the only route to this output.";
        }

        return to.Output.CanNativePassthrough(from.Source, out string reason) ? null : reason;
    }

    private void AutoEnableNativeRoutes()
    {
        if (!_settings.PreferNativePassthrough)
        {
            return;
        }

        foreach (var outputEntry in _nodes.Values.Where(entry => entry.Output != null).ToList())
        {
            var output = outputEntry.Output!;
            if (output.IsRecording)
            {
                continue;
            }

            var direct = _links
                .Where(link => link.To == outputEntry.Id && link.Kind == "direct")
                .ToList();
            if (direct.Count != 1)
            {
                continue;
            }

            var link = direct[0];
            if (link.Native || link.NativePreference == false ||
                !link.Enabled || Math.Abs(link.Gain - 1f) > 1e-4f ||
                !_nodes.TryGetValue(link.From, out var from) || from.Source == null)
            {
                continue;
            }

            if (!output.CanNativePassthrough(from.Source, out _))
            {
                continue;
            }

            link.Native = true;
            if (!RefreshNativeChain(output, quiet: true))
            {
                link.Native = false;
            }
        }
    }

    private void ActivateNativeRoutes()
    {
        foreach (var link in _links.Where(item => item.Native && item.Kind == "direct").ToList())
        {
            if (!_nodes.TryGetValue(link.From, out var from) || from.Source == null ||
                !_nodes.TryGetValue(link.To, out var to) || to.Output == null)
            {
                link.Native = false;
                continue;
            }

            string? error = _graph.SetNativePassthrough(to.Output, from.Source, true);
            if (error != null)
            {
                link.Native = false;
                SetStatus($"Native passthrough unavailable for {to.Title}: {error}", isError: true);
            }
        }
    }
}
