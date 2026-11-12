using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    /// <summary>
    /// Snapshot of the effect chains used for an offline timeline bounce. The
    /// live graph nodes are cloned so the render cannot disturb realtime state.
    /// </summary>
    private sealed class TimelineBouncePlan : IDisposable
    {
        public Dictionary<string, List<IAudioProcessor>> Tracks { get; } = new(StringComparer.Ordinal);
        public List<IAudioProcessor> Master { get; } = new();
        public bool SkippedVst3 { get; set; }
        public bool SkippedProcessor { get; set; }

        public void ProcessTrack(TimelineTrack track, float[] buffer, int frames)
        {
            if (Tracks.TryGetValue(track.Id, out var chain))
            {
                ProcessChain(chain, buffer, frames);
            }
        }

        public void ProcessMaster(float[] buffer, int frames) => ProcessChain(Master, buffer, frames);

        private static void ProcessChain(List<IAudioProcessor> chain, float[] buffer, int frames)
        {
            if (chain.Count == 0)
            {
                return;
            }

            const int chunkFrames = 1024;
            for (int offset = 0; offset < frames; offset += chunkFrames)
            {
                int count = Math.Min(chunkFrames, frames - offset);
                var span = buffer.AsSpan(offset * 2, count * 2);
                foreach (var processor in chain)
                {
                    processor.Process(span, count);
                }
            }
        }

        public void Dispose()
        {
            var seen = new HashSet<IAudioProcessor>(ReferenceEqualityComparer.Instance);
            foreach (var chain in Tracks.Values)
            {
                DisposeChain(chain, seen);
            }

            DisposeChain(Master, seen);
        }

        private static void DisposeChain(List<IAudioProcessor> chain, HashSet<IAudioProcessor> seen)
        {
            foreach (var processor in chain)
            {
                if (seen.Add(processor))
                {
                    (processor as IDisposable)?.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Walks each timeline track to its first output, clones the modifiers on
    /// the way, and separates the suffix shared by every track (the master bus)
    /// from per-track chains.
    /// </summary>
    private TimelineBouncePlan BuildTimelineBouncePlan()
    {
        var plan = new TimelineBouncePlan();
        if (_timeline == null)
        {
            return plan;
        }

        var paths = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var track in _timeline.Project.Tracks)
        {
            if (string.IsNullOrWhiteSpace(track.SourceNodeId))
            {
                continue;
            }

            var path = FindTimelinePathToOutput(track.SourceNodeId);
            if (path is { Count: > 0 })
            {
                paths[track.Id] = path;
            }
        }

        var allPaths = paths.Values.ToList();
        var masterIds = new List<string>();
        if (allPaths.Count > 0)
        {
            int common = 0;
            int shortest = allPaths.Min(path => path.Count);
            while (common < shortest)
            {
                string id = allPaths[0][allPaths[0].Count - 1 - common];
                if (allPaths.All(path => path[path.Count - 1 - common] == id))
                {
                    common++;
                }
                else
                {
                    break;
                }
            }

            if (common > 0)
            {
                masterIds = allPaths[0].Skip(allPaths[0].Count - common).ToList();
                foreach (string id in masterIds)
                {
                    AddModifierProcessors(plan.Master, id, plan);
                }
            }
        }

        foreach (var (trackId, path) in paths)
        {
            bool sharesMaster = masterIds.Count > 0 &&
                path.Count >= masterIds.Count &&
                path.Skip(path.Count - masterIds.Count).SequenceEqual(masterIds);
            var trackPath = sharesMaster ? path.Take(path.Count - masterIds.Count) : path;
            var chain = new List<IAudioProcessor>();
            foreach (string id in trackPath)
            {
                AddModifierProcessors(chain, id, plan);
            }

            if (chain.Count > 0)
            {
                plan.Tracks[trackId] = chain;
            }
        }

        return plan;
    }

    private List<string>? FindTimelinePathToOutput(string startId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { startId };
        var queue = new Queue<(string Id, List<string> Path)>();
        queue.Enqueue((startId, new List<string>()));

        while (queue.Count > 0)
        {
            var (id, path) = queue.Dequeue();
            if (_nodes.TryGetValue(id, out var entry) && entry.Output != null)
            {
                return path;
            }

            foreach (var link in _links)
            {
                if (link.From != id || !link.Enabled ||
                    string.Equals(link.Kind, "sidechain", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!_nodes.TryGetValue(link.To, out var next) || !visited.Add(next.Id))
                {
                    continue;
                }

                var nextPath = new List<string>(path);
                if (next.Modifier != null)
                {
                    nextPath.Add(next.Id);
                }

                queue.Enqueue((next.Id, nextPath));
            }
        }

        return null;
    }

    private void AddModifierProcessors(List<IAudioProcessor> chain, string modifierId,
        TimelineBouncePlan plan)
    {
        if (!_nodes.TryGetValue(modifierId, out var entry) ||
            entry.Modifier is not { } modifier || !modifier.Enabled)
        {
            return;
        }

        var clones = new List<IAudioProcessor>();
        foreach (var processor in modifier.Processors)
        {
            if (processor is ITapProcessor)
            {
                continue;
            }

            if (processor is IVst3Processor)
            {
                plan.SkippedVst3 = true;
                continue;
            }

            try
            {
                var spec = PresetSerializer.SerializeProcessor(processor);
                var clone = PresetSerializer.CreateProcessor(spec);
                if (clone != null)
                {
                    clones.Add(clone);
                }
            }
            catch
            {
                plan.SkippedProcessor = true;
            }
        }

        if (clones.Count == 0)
        {
            return;
        }

        if (modifier.Parallel)
        {
            chain.Add(new ParallelChainProcessor(clones.ToArray(), modifier.Send));
        }
        else
        {
            chain.AddRange(clones);
        }
    }
}
