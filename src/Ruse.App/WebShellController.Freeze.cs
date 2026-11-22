using System.IO;
using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private readonly HashSet<string> _freezingSources = new(StringComparer.Ordinal);

    private sealed record SourceChain(NodeEntry Target, List<LinkEntry> Links);

    private SourceChain? FindSourceChain(string sourceId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { sourceId };
        var path = new List<LinkEntry>();
        NodeEntry? target = null;

        bool Walk(string from)
        {
            foreach (var link in _links)
            {
                if (!link.Enabled || link.From != from || link.Kind == "sidechain")
                {
                    continue;
                }

                if (!_nodes.TryGetValue(link.To, out var to))
                {
                    continue;
                }

                if ((link.Kind == "direct" || link.Kind == "modifier-output") && to.Output != null)
                {
                    path.Add(link);
                    target = to;
                    return true;
                }

                if ((link.Kind == "source-modifier" || link.Kind == "modifier-modifier") &&
                    to.Modifier != null && visited.Add(to.Id))
                {
                    path.Add(link);
                    if (Walk(to.Id))
                    {
                        return true;
                    }

                    path.RemoveAt(path.Count - 1);
                    visited.Remove(to.Id);
                }
            }

            return false;
        }

        return Walk(sourceId) && target != null ? new SourceChain(target, path) : null;
    }

    private bool IsExclusiveSourceRoute(string sourceId, SourceChain chain)
    {
        var pathIds = chain.Links.Select(link => link.Id).ToHashSet(StringComparer.Ordinal);
        var routeNodes = chain.Links
            .Where(link => link.Kind is "source-modifier" or "modifier-modifier")
            .Select(link => link.To)
            .Append(sourceId)
            .ToHashSet(StringComparer.Ordinal);
        return !_links.Any(link =>
            link.Enabled &&
            !pathIds.Contains(link.Id) &&
            (routeNodes.Contains(link.From) || routeNodes.Contains(link.To)));
    }

    private bool CanFreezeSourceChain(string sourceId, SourceChain chain)
    {
        if (!IsExclusiveSourceRoute(sourceId, chain))
        {
            return false;
        }

        var modifierIds = chain.Links
            .Where(link => link.Kind is "source-modifier" or "modifier-modifier")
            .Select(link => link.To)
            .ToHashSet(StringComparer.Ordinal);
        if (_links.Any(link => link.Kind == "sidechain" &&
                (modifierIds.Contains(link.To) || modifierIds.Contains(link.From))))
        {
            return false;
        }

        foreach (string modifierId in modifierIds)
        {
            if (!_nodes.TryGetValue(modifierId, out var entry) || entry.Modifier == null)
            {
                continue;
            }

            if (entry.Modifier.Processors.Any(processor => processor is IVst3Processor))
            {
                return false;
            }
        }

        return true;
    }

    private OfflineTimelineBounce? BuildSourceFreezeGraph(
        string sourceId, out NodeEntry? target, out string error)
    {
        target = null;
        error = "";
        if (!_nodes.TryGetValue(sourceId, out var entry) ||
            entry.Source is not { Kind: SourceKind.File } source ||
            string.IsNullOrWhiteSpace(source.FilePath))
        {
            error = "Only loaded file or sample sources can be frozen.";
            return null;
        }

        var chain = FindSourceChain(sourceId);
        if (chain == null)
        {
            error = "The source needs a route to an output before its effects can be frozen.";
            return null;
        }

        target = chain.Target;
        if (target.Output == null)
        {
            error = "The source route has no output.";
            return null;
        }

        if (!IsExclusiveSourceRoute(sourceId, chain))
        {
            error = "Freeze needs a dedicated source route. Disconnect other cables from this source or its effects first.";
            return null;
        }

        var modifierIds = chain.Links
            .Where(link => link.Kind is "source-modifier" or "modifier-modifier")
            .Select(link => link.To)
            .ToHashSet(StringComparer.Ordinal);
        if (_links.Any(link => link.Kind == "sidechain" &&
                (modifierIds.Contains(link.To) || modifierIds.Contains(link.From))))
        {
            error = "Freeze does not support routes that use sidechain keys.";
            return null;
        }

        var bounce = new OfflineTimelineBounce
        {
            Output = OutputNode.CreateForTest(static (_, _) => { }),
        };
        bounce.Output.EnableGraphEngine();
        bounce.TailSeconds = source.FileLoop
            ? 0
            : Math.Clamp(_settings.TimelineBounceTailSeconds, 0, 30);

        try
        {
            var track = bounce.Timeline.AddTrack(entry.Title);
            if (!bounce.Timeline.AddClip(track.Id, source.FilePath, out string clipError, 0))
            {
                error = clipError;
                bounce.Dispose();
                return null;
            }

            var timelineSource = new SourceNode(entry.Title, SourceKind.Timeline);
            bounce.Graph.AddSource(timelineSource);
            bounce.Timeline.AttachSource(track.Id, timelineSource);
            timelineSource.Start();
            bounce.Graph.Timeline = bounce.Timeline;
            bounce.Graph.AddOutputNode(bounce.Output);

            var modifiers = new Dictionary<string, ModifierNode>(StringComparer.Ordinal);
            foreach (var link in chain.Links)
            {
                if (link.Kind is not ("source-modifier" or "modifier-modifier") ||
                    !_nodes.TryGetValue(link.To, out var live) ||
                    live.Modifier is not { } liveModifier ||
                    modifiers.ContainsKey(link.To))
                {
                    continue;
                }

                var modifier = new ModifierNode(live.Title)
                {
                    Enabled = liveModifier.Enabled,
                    Parallel = liveModifier.Parallel,
                    Send = liveModifier.Send,
                };
                foreach (var processor in liveModifier.Processors)
                {
                    if (processor is ITapProcessor)
                    {
                        continue;
                    }

                    if (processor is IVst3Processor)
                    {
                        error = "Freeze does not support VST3 effects in the routed chain.";
                        bounce.Dispose();
                        return null;
                    }

                    try
                    {
                        var clone = PresetSerializer.CreateProcessor(
                            PresetSerializer.SerializeProcessor(processor));
                        if (clone == null)
                        {
                            throw new InvalidOperationException("Unsupported effect.");
                        }

                        modifier.Add(clone);
                    }
                    catch (Exception ex)
                    {
                        error = $"Could not clone {processor.DisplayName}: {ex.Message}";
                        bounce.Dispose();
                        return null;
                    }
                }

                bounce.Graph.AddModifier(modifier);
                modifiers[link.To] = modifier;
            }

            var currentSource = timelineSource;
            ModifierNode? currentModifier = null;
            foreach (var link in chain.Links)
            {
                if (link.Kind == "direct")
                {
                    bounce.Graph.Connect(currentSource, bounce.Output, link.Gain);
                }
                else if (link.Kind == "source-modifier" && modifiers.TryGetValue(link.To, out var first))
                {
                    bounce.Graph.ConnectSourceToModifier(currentSource, first);
                    currentModifier = first;
                }
                else if (link.Kind == "modifier-modifier" &&
                         currentModifier != null && modifiers.TryGetValue(link.To, out var next))
                {
                    bounce.Graph.ConnectModifierToModifier(currentModifier, next);
                    currentModifier = next;
                }
                else if (link.Kind == "modifier-output" && currentModifier != null)
                {
                    bounce.Graph.ConnectModifierToOutput(currentModifier, bounce.Output, link.Gain);
                }
            }

            return bounce;
        }
        catch
        {
            bounce.Dispose();
            throw;
        }
    }

    private bool IsFrozenFileReferenced(string path)
    {
        foreach (var entry in _nodes.Values)
        {
            if (string.Equals(entry.Source?.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return _timeline?.Project.Tracks.Any(track =>
            track.Clips.Any(clip =>
                string.Equals(clip.SourcePath, path, StringComparison.OrdinalIgnoreCase))) == true;
    }

    private void ReleaseFrozenProxyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !FrozenProxyStore.IsTracked(path))
        {
            return;
        }

        if (IsFrozenFileReferenced(path))
        {
            return;
        }

        FrozenProxyStore.MoveToTrash(path);
    }

    private void CleanupUnreferencedFrozenProxies()
    {
        if (App.DemoMode || App.LiveDemoMode) return;
        foreach (string path in FrozenProxyStore.TrackedPaths())
        {
            ReleaseFrozenProxyPath(path);
        }
    }

    private static void RestoreFrozenProxyPath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && TrustedPath.IsLocalFile(path))
        {
            FrozenProxyStore.Restore(path);
        }
    }

    private async void FreezeSourceEffects(string id)
    {
        if (!_freezingSources.Add(id))
        {
            SetStatus("Freeze is already running for this source.", isError: true);
            return;
        }

        PublishGraph();

        try
        {
            await FreezeSourceEffectsCore(id);
        }
        catch (Exception ex)
        {
            SetStatus("Could not freeze effects: " + ex.Message, isError: true);
        }
        finally
        {
            _freezingSources.Remove(id);
            PublishGraph();
        }
    }

    private async Task FreezeSourceEffectsCore(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Source is not { Kind: SourceKind.File } source ||
            string.IsNullOrWhiteSpace(source.FilePath))
        {
            SetStatus("Only loaded file or sample sources can be frozen.", isError: true);
            return;
        }

        OfflineTimelineBounce? bounce = null;
        NodeEntry? target = null;
        string error = "";
        string path = "";
        Interlocked.Increment(ref _offlinePrepareCount);
        try
        {
            var prepared = await Task.Run(() =>
            {
                var preparedBounce = BuildSourceFreezeGraph(id, out var preparedTarget, out var preparedError);
                return (preparedBounce, preparedTarget, preparedError);
            }).ConfigureAwait(true);
            bounce = prepared.preparedBounce;
            target = prepared.preparedTarget;
            error = prepared.preparedError;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            Interlocked.Decrement(ref _offlinePrepareCount);
        }

        if (bounce == null || target == null)
        {
            SetStatus("Could not prepare freeze: " + (string.IsNullOrWhiteSpace(error) ? "unsupported route." : error), isError: true);
            return;
        }

        bool ok;
        string renderError;
        try
        {
            string directory = RecordingDirectory();
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory,
                SanitizeFileName(entry.Title) + "-frozen-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".wav");
            SetStatus("Freezing effects...");
            (ok, renderError) = await Task.Run(() =>
            {
                bool success = bounce.Render(path, out string message);
                return (success, message);
            });
        }
        catch (Exception ex)
        {
            ok = false;
            renderError = ex.Message;
        }
        finally
        {
            bounce.Dispose();
        }

        if (!ok)
        {
            SetStatus("Could not freeze effects: " + renderError, isError: true);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }

        if (_disposed)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }

        var chainValue = FindSourceChain(id);
        if (chainValue == null)
        {
            SetStatus("The source route changed before it could be frozen.", isError: true);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }

        if (!IsExclusiveSourceRoute(id, chainValue))
        {
            SetStatus("Freeze needs a dedicated source route; the graph changed before the render finished.", isError: true);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }

        SourceNode proxy;
        try
        {
            proxy = new SourceNode(entry.Title + " (frozen)", SourceKind.File)
            {
                Gain = source.Gain,
                Muted = source.Muted,
                GroupKey = source.GroupKey,
            };
            proxy.SetFile(path, source.FileLoop);
            FrozenProxyStore.Track(path);
        }
        catch (Exception ex)
        {
            SetStatus("Could not load the frozen file: " + ex.Message, isError: true);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }

        var position = FindFreeNodePosition(entry.X + 280, entry.Y + 40, 220, 180);

        var proxyEntry = RegisterSource(proxy, position.X, position.Y);
        proxyEntry.Title = entry.Title + " (frozen)";
        proxy.Title = proxyEntry.Title;
        proxyEntry.Subtitle = "Frozen effect render";
        var proxyLink = ConnectEntries(proxyEntry, target, publish: false);

        foreach (var oldLink in chainValue.Links)
        {
            RemoveLinkInternal(oldLink);
        }

        PushAddNode(proxyEntry, new[] { proxyLink });
        entry.Source.Muted = true;
        PublishNodeState(entry.Id);
        PublishGraph();
        PublishTelemetry(force: true);
        SetStatus($"Frozen effects to {Path.GetFileName(path)}; original effect route bypassed.");

    }
}
