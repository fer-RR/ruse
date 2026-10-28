using NAudio.CoreAudioApi;

namespace Ruse.App.Audio;

/// <summary>Owns the routing graph and rebuilds route processor chains.</summary>
public sealed class AudioGraph : IDisposable
{
    /// <summary>Start capture/render threads as soon as a route exists.</summary>
    public bool AutoRun { get; set; } = true;

    public List<SourceNode> Sources { get; } = new();
    public List<OutputNode> Outputs { get; } = new();
    public List<ModifierNode> Modifiers { get; } = new();

    // Direct source â†’ output links. Routes that only exist because a modifier
    // chain reaches the output are derived from the modifier graph instead, so
    // removing a modifier really does remove the audio it carried.
    private readonly Dictionary<OutputNode, HashSet<SourceNode>> _directLinks = new();

    // Hidden real-time sinks for node-level taps.
    private readonly Dictionary<ModifierNode, OutputNode> _tapMonitors = new();
    private bool _refreshingTapMonitors;

    public OutputNode AddOutput(MMDevice device)
    {
        var node = new OutputNode(device);
        Outputs.Add(node);
        return node;
    }

    /// <summary>Adds an output that renders to an ASIO driver.</summary>
    public OutputNode AddOutput(AsioDriverInfo driver)
    {
        var node = new OutputNode(driver);
        Outputs.Add(node);
        return node;
    }

    /// <summary>Creates a device-less output that pushes mixed frames to a sink (tests/benchmarks).</summary>
    public OutputNode AddOutputForTest(Action<float[], int> sink)
    {
        var node = OutputNode.CreateForTest(sink);
        Outputs.Add(node);
        return node;
    }

    /// <summary>Adds an output that records the mix to an audio file.</summary>
    public OutputNode AddOutputForFile(string path, AudioFileFormat format)
    {
        var node = OutputNode.CreateForFile(path, format);
        Outputs.Add(node);
        return node;
    }

    public void RemoveOutput(OutputNode node)
    {
        Outputs.Remove(node);
        _directLinks.Remove(node);
        foreach (var modifier in Modifiers)
        {
            modifier.Outputs.Remove(node);
        }

        node.Dispose();
        RefreshTapMonitors();
    }

    public void AddSource(SourceNode source)
    {
        Sources.Add(source);
    }

    public void AddModifier(ModifierNode modifier)
    {
        Modifiers.Add(modifier);
    }

    public void RemoveModifier(ModifierNode modifier)
    {
        modifier.Inputs.Clear();
        modifier.Outputs.Clear();
        Modifiers.Remove(modifier);

        // Drop dangling references so removed nodes can be collected.
        foreach (var other in Modifiers)
        {
            other.Next.Remove(modifier);
        }

        // Rebuild every output: the routes that only existed because this
        // modifier reached them are gone for good.
        foreach (var output in Outputs)
        {
            SyncOutputRoutes(output);
        }

        RefreshTapMonitors();
    }

    /// <summary>Starts a source's capture thread if it is not already running.</summary>
    public void EnsureRunning(SourceNode source)
    {
        if (!AutoRun || source.IsRunning)
        {
            return;
        }

        source.Start();
    }

    /// <summary>Starts an output's render thread if it is not already running.</summary>
    public void EnsureRunning(OutputNode output)
    {
        if (!AutoRun || output.IsRunning || !output.AutoStart)
        {
            return;
        }

        if (output.IsFileRecorder && output.RecorderMode != RecorderMode.On)
        {
            return;
        }

        output.Start();
    }

    public void RemoveSource(SourceNode source)
    {
        foreach (var output in Outputs)
        {
            if (_directLinks.TryGetValue(output, out var links))
            {
                links.Remove(source);
            }
        }

        foreach (var modifier in Modifiers)
        {
            modifier.Inputs.Remove(source);
        }

        Sources.Remove(source);

        foreach (var output in Outputs)
        {
            SyncOutputRoutes(output);
        }

        source.Dispose();
    }

    public void Connect(SourceNode source, OutputNode output, float gain = 1.0f)
    {
        if (!_directLinks.TryGetValue(output, out var links))
        {
            links = new HashSet<SourceNode>();
            _directLinks[output] = links;
        }

        links.Add(source);

        // Keep the existing route object when there is one, so per-route gain
        // and effect state survive a re-connect.
        var route = output.Inputs.FirstOrDefault(r => r.Source == source);
        if (route == null)
        {
            output.Inputs.Add(new OutputNode.Route { Source = source, Gain = gain });
        }

        RebuildRouteProcessors(output);

        // Auto-run: as soon as a route exists, make sure both ends are live.
        EnsureRunning(source);
        EnsureRunning(output);
    }

    public void Disconnect(SourceNode source, OutputNode output)
    {
        if (_directLinks.TryGetValue(output, out var links))
        {
            links.Remove(source);
        }

        SyncOutputRoutes(output);
    }

    /// <summary>
    /// Recomputes which sources reach an output. A source belongs to the output
    /// when it is wired straight to it, or when it reaches it through a chain of
    /// modifiers. Anything else is removed, so unplugging or deleting a node
    /// actually stops the audio it was carrying.
    /// </summary>
    public void SyncOutputRoutes(OutputNode output)
    {
        var wanted = new HashSet<SourceNode>();
        foreach (var source in Sources)
        {
            if (DirectlyConnected(source, output) || ReachesThroughModifier(source, output))
            {
                wanted.Add(source);
            }
        }

        output.Inputs.RemoveAll(r => r.Source == null || !wanted.Contains(r.Source));

        foreach (var source in wanted)
        {
            if (!output.Inputs.Any(r => r.Source == source))
            {
                output.Inputs.Add(new OutputNode.Route { Source = source });
            }
        }

        RebuildRouteProcessors(output);
    }

    private bool DirectlyConnected(SourceNode source, OutputNode output)
    {
        return _directLinks.TryGetValue(output, out var links) && links.Contains(source);
    }

    /// <summary>True when a modifier fed by the source can still reach the output.</summary>
    private bool ReachesThroughModifier(SourceNode source, OutputNode output)
    {
        foreach (var modifier in Modifiers)
        {
            if (modifier.Inputs.Contains(source) && IsReachableToOutput(modifier, output, new HashSet<ModifierNode>()))
            {
                return true;
            }
        }

        return false;
    }

    // ---------- Modifier wiring ----------

    /// <summary>Routes a source through a modifier (source â†’ modifier).</summary>
    public void ConnectSourceToModifier(SourceNode source, ModifierNode modifier)
    {
        if (!modifier.Inputs.Contains(source))
        {
            modifier.Inputs.Add(source);
        }

        // If the modifier is already wired to outputs, make sure those outputs
        // now render this source through it.
        foreach (var output in ReachableOutputs(modifier))
        {
            SyncOutputRoutes(output);
        }

        EnsureRunning(source);
        RefreshTapMonitors();
    }

    /// <summary>Sends a modifier's output to an output node (modifier â†’ output).</summary>
    public void ConnectModifierToOutput(ModifierNode modifier, OutputNode output)
    {
        if (!modifier.Outputs.Contains(output))
        {
            modifier.Outputs.Add(output);
        }

        // Ensure a route exists for every source that reaches this modifier,
        // including sources entering through upstream modifier chains.
        foreach (var source in UpstreamSources(modifier))
        {
            EnsureRunning(source);
        }

        SyncOutputRoutes(output);
        EnsureRunning(output);
        RefreshTapMonitors();
    }

    /// <summary>Chains two modifiers together (modifier â†’ modifier).</summary>
    public void ConnectModifierToModifier(ModifierNode from, ModifierNode to)
    {
        if (!from.Next.Contains(to))
        {
            from.Next.Add(to);
        }

        foreach (var output in ReachableOutputs(from).Concat(ReachableOutputs(to)).Distinct())
        {
            SyncOutputRoutes(output);
        }

        RefreshTapMonitors();
    }

    public void DisconnectModifierFromModifier(ModifierNode from, ModifierNode to)
    {
        from.Next.Remove(to);

        foreach (var output in ReachableOutputs(to).Concat(ReachableOutputs(from)).Distinct())
        {
            SyncOutputRoutes(output);
        }

        RefreshTapMonitors();
    }

    /// <summary>Every output reachable from a modifier by following Outputs and Next links.</summary>
    private static IEnumerable<OutputNode> ReachableOutputs(ModifierNode modifier)
    {
        var seen = new HashSet<ModifierNode>();
        var results = new List<OutputNode>();

        void Walk(ModifierNode node)
        {
            if (!seen.Add(node))
            {
                return;
            }

            foreach (var output in node.Outputs)
            {
                if (!results.Contains(output))
                {
                    results.Add(output);
                }
            }

            foreach (var next in node.Next)
            {
                Walk(next);
            }
        }

        Walk(modifier);
        return results;
    }

    /// <summary>Sources feeding a modifier, including via upstream modifier chains.</summary>
    private IEnumerable<SourceNode> UpstreamSources(ModifierNode target)
    {
        var results = new List<SourceNode>();
        foreach (var modifier in Modifiers)
        {
            if (!ReferenceEquals(modifier, target) && !CanReachForward(modifier, target))
            {
                continue;
            }

            foreach (var source in modifier.Inputs)
            {
                if (!results.Contains(source))
                {
                    results.Add(source);
                }
            }
        }

        return results;
    }

    /// <summary>True if <paramref name="from"/> reaches <paramref name="target"/> along Next links.</summary>
    private static bool CanReachForward(ModifierNode from, ModifierNode target)
    {
        var seen = new HashSet<ModifierNode>();

        bool Walk(ModifierNode node)
        {
            foreach (var next in node.Next)
            {
                if (ReferenceEquals(next, target))
                {
                    return true;
                }

                if (seen.Add(next) && Walk(next))
                {
                    return true;
                }
            }

            return false;
        }

        return Walk(from);
    }

    public void DisconnectModifierFromOutput(ModifierNode modifier, OutputNode output)
    {
        modifier.Outputs.Remove(output);
        SyncOutputRoutes(output);
        RefreshTapMonitors();
    }

    public void DisconnectSourceFromModifier(SourceNode source, ModifierNode modifier)
    {
        modifier.Inputs.Remove(source);

        foreach (var output in ReachableOutputs(modifier))
        {
            SyncOutputRoutes(output);
        }

        RefreshTapMonitors();
    }

    /// <summary>Keeps a real-time monitor alive for every tap processor.</summary>
    private void RefreshTapMonitors()
    {
        if (_refreshingTapMonitors)
        {
            return;
        }

        _refreshingTapMonitors = true;
        try
        {
            var wanted = new HashSet<ModifierNode>();
            foreach (var modifier in Modifiers)
            {
                int[] transcriptionTaps = modifier.Processors
                    .OfType<TranscriptionProcessor>()
                    .Select(processor => processor.TapId)
                    .Where(id => id > 0)
                    .ToArray();
                var waveformTaps = modifier.Processors
                    .OfType<WaveformProcessor>()
                    .Where(processor => processor.TapId > 0)
                    .ToArray();
                int[] dbMeterTaps = modifier.Processors
                    .OfType<DbMeterProcessor>()
                    .Select(processor => processor.TapId)
                    .Where(id => id > 0)
                    .ToArray();

                if (transcriptionTaps.Length == 0 && waveformTaps.Length == 0 && dbMeterTaps.Length == 0)
                {
                    continue;
                }

                wanted.Add(modifier);
                if (!_tapMonitors.TryGetValue(modifier, out var monitor))
                {
                    monitor = OutputNode.CreateMonitor((samples, length) =>
                    {
                        if (!modifier.Enabled)
                        {
                            return;
                        }

                        int frames = length / 2;
                        var block = samples.AsSpan(0, length);
                        for (int i = 0; i < transcriptionTaps.Length; i++)
                        {
                            TranscriptionProcessor.WriteTap(transcriptionTaps[i], block, frames);
                        }

                        for (int i = 0; i < waveformTaps.Length; i++)
                        {
                            var waveform = waveformTaps[i];
                            WaveformProcessor.WriteTap(waveform.TapId, block, frames,
                                waveform.Gain, waveform.Smoothing, waveform.Speed);
                        }

                        for (int i = 0; i < dbMeterTaps.Length; i++)
                        {
                            DbMeterProcessor.WriteTap(dbMeterTaps[i], block, frames);
                        }
                    });
                    _tapMonitors[modifier] = monitor;
                    Outputs.Add(monitor);
                }

                if (!modifier.Outputs.Contains(monitor))
                {
                    modifier.Outputs.Add(monitor);
                }

                foreach (var source in UpstreamSources(modifier))
                {
                    EnsureRunning(source);
                }

                SyncOutputRoutes(monitor);
                EnsureRunning(monitor);
            }

            foreach (var pair in _tapMonitors.ToList())
            {
                if (wanted.Contains(pair.Key))
                {
                    continue;
                }

                pair.Key.Outputs.Remove(pair.Value);
                _tapMonitors.Remove(pair.Key);
                Outputs.Remove(pair.Value);
                _directLinks.Remove(pair.Value);
                pair.Value.Dispose();
            }
        }
        finally
        {
            _refreshingTapMonitors = false;
        }
    }

    /// <summary>Starts every tap monitor, used when routing is switched on.</summary>
    public void EnsureMonitorsRunning()
    {
        foreach (var monitor in _tapMonitors.Values)
        {
            EnsureRunning(monitor);
        }
    }

    /// <summary>Rebuilds each route's private effect chain and alignment delay.</summary>
    public void RebuildRouteProcessors(OutputNode output)
    {
        var chains = new List<(OutputNode.Route Route, IAudioProcessor[] Templates, int Latency)>();
        int maxLatency = 0;

        foreach (var route in output.Inputs)
        {
            if (route.Source == null)
            {
                continue;
            }

            var chain = new List<IAudioProcessor>();
            CollectChain(route.Source, output, chain, new HashSet<ModifierNode>());
            var templates = chain.ToArray();

            int latency = 0;
            for (int i = 0; i < templates.Length; i++)
            {
                latency += templates[i].LatencySamples;
            }

            maxLatency = Math.Max(maxLatency, latency);
            chains.Add((route, templates, latency));
        }

        foreach (var (route, templates, latency) in chains)
        {
            route.SetChain(templates, maxLatency - latency);
        }

        output.RefreshRoutes();
    }

    /// <summary>Walks modifiers in signal order from source to output.</summary>
    private void CollectChain(SourceNode source, OutputNode output, List<IAudioProcessor> chain, HashSet<ModifierNode> visited)
    {
        foreach (var modifier in Modifiers)
        {
            if (!modifier.Inputs.Contains(source))
            {
                continue;
            }

            AppendModifier(modifier, output, chain, visited);
        }
    }

    /// <summary>Appends a modifier's effects when it reaches the output.</summary>
    private static void AppendModifier(ModifierNode modifier, OutputNode output, List<IAudioProcessor> chain, HashSet<ModifierNode> visited)
    {
        if (!visited.Add(modifier) || !IsReachableToOutput(modifier, output, new HashSet<ModifierNode>()))
        {
            return;
        }

        if (modifier.Enabled)
        {
            foreach (var processor in modifier.Processors)
            {
                // Taps are fed once by their monitor.
                if (processor is ITapProcessor)
                {
                    continue;
                }

                chain.Add(processor);
            }
        }

        foreach (var next in modifier.Next)
        {
            AppendModifier(next, output, chain, visited);
        }
    }

    /// <summary>True when a modifier can reach the output directly or via chained modifiers.</summary>
    private static bool IsReachableToOutput(ModifierNode modifier, OutputNode output, HashSet<ModifierNode> visited)
    {
        if (!visited.Add(modifier))
        {
            return false;
        }

        if (modifier.Outputs.Contains(output))
        {
            return true;
        }

        foreach (var next in modifier.Next)
        {
            if (IsReachableToOutput(next, output, visited))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var output in Outputs)
        {
            output.Dispose();
        }

        foreach (var source in Sources)
        {
            source.Dispose();
        }

        _tapMonitors.Clear();
        _directLinks.Clear();
        Outputs.Clear();
        Sources.Clear();
        Modifiers.Clear();
    }
}
