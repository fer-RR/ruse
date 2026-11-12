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
    public TimelineService? Timeline { get; set; }

    // Direct source â†’ output links. Routes that only exist because a modifier
    // chain reaches the output are derived from the modifier graph instead, so
    // removing a modifier really does remove the audio it carried.
    private readonly Dictionary<OutputNode, HashSet<SourceNode>> _directLinks = new();

    // Single-clock renderer shared by every output; see GraphEngine.
    private GraphEngine? _engine;

    /// <summary>Starts the graph renderer (idempotent).</summary>
    public void StartEngine()
    {
        if (_engine == null)
        {
            _engine = new GraphEngine(this);
        }

        _engine.Start();
    }

    /// <summary>Stops the graph renderer and releases its subscriptions.</summary>
    public void StopEngine() => _engine?.Stop();

    internal void RenderEngineOnceForTest()
    {
        _engine ??= new GraphEngine(this);
        _engine.RenderOnceForTest();
    }

    /// <summary>Number of tap workers (diagnostics/self-test).</summary>
    public int TapWorkerCount => _engine?.TapWorkerCount ?? 0;

    private void InvalidateEngine(Action? afterDrain = null)
    {
        if (_engine != null)
        {
            _engine.Invalidate(afterDrain);
        }
        else
        {
            afterDrain?.Invoke();
        }
    }

    /// <summary>Serializes a graph mutation with snapshot rebuilds.</summary>
    public void WithEngineLock(Action action)
    {
        if (_engine != null)
        {
            _engine.RunExclusive(action);
        }
        else
        {
            action();
        }
    }

    /// <summary>Sources wired directly to an output.</summary>
    internal IEnumerable<SourceNode> DirectSources(OutputNode output) =>
        _directLinks.TryGetValue(output, out var links) ? links : Enumerable.Empty<SourceNode>();

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

    /// <summary>Adds an existing output node, invalidating the renderer if asked.</summary>
    public void AddOutputNode(OutputNode node, bool invalidate = false)
    {
        bool added = false;
        WithEngineLock(() =>
        {
            if (!Outputs.Contains(node))
            {
                Outputs.Add(node);
                added = true;
            }
        });

        if (invalidate && added)
        {
            InvalidateEngine();
        }
    }

    public void RemoveOutput(OutputNode node, bool invalidateEngine = true)
    {
        bool removed = false;
        WithEngineLock(() =>
        {
            removed = Outputs.Remove(node);
            _directLinks.Remove(node);
            foreach (var modifier in Modifiers)
            {
                modifier.Outputs.Remove(node);
            }
        });

        // Compile without the output before disposing it, so the renderer can
        // never call into a disposed endpoint. Both the compile and the dispose
        // happen outside the engine lock now.
        if (removed && invalidateEngine)
        {
            InvalidateEngine(node.Dispose);
        }
        else
        {
            node.Dispose();
        }
    }

    public void AddSource(SourceNode source)
    {
        Sources.Add(source);
    }

    public void AddModifier(ModifierNode modifier)
    {
        Modifiers.Add(modifier);
    }

    public void RemoveModifier(ModifierNode modifier, bool invalidateEngine = true)
    {
        WithEngineLock(() =>
        {
            modifier.Inputs.Clear();
            modifier.Outputs.Clear();
            Modifiers.Remove(modifier);

            // Drop dangling references so removed nodes can be collected.
            foreach (var other in Modifiers)
            {
                other.Next.Remove(modifier);
                other.SidechainModifiers.Remove(modifier);
            }
        });

        // Rebuild every output: the routes that only existed because this
        // modifier reached them are gone for good. An unconnected modifier is
        // absent from the compiled graph, so rebuilding would only reset the
        // running renderer and cause an audible click.
        if (invalidateEngine)
        {
            foreach (var output in Outputs)
            {
                SyncOutputRoutes(output);
            }

            InvalidateEngine(modifier.Dispose);
        }
        else
        {
            modifier.Dispose();
        }
    }

    /// <summary>Starts a source's capture thread if it is not already running.</summary>
    public void EnsureRunning(SourceNode source)
    {
        if (!AutoRun || source.IsRunning)
        {
            return;
        }

        source.Start();
        StartEngine();
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
        StartEngine();
    }

    public void RemoveSource(SourceNode source, bool invalidateEngine = true)
    {
        WithEngineLock(() =>
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
                modifier.SidechainInputs.Remove(source);
            }

            Sources.Remove(source);
        });

        if (invalidateEngine)
        {
            foreach (var output in Outputs)
            {
                SyncOutputRoutes(output);
            }

            // The renderer may still hold a subscription until the rebuild
            // above; dispose the source only after it has been dropped.
            InvalidateEngine(source.Dispose);
        }
        else
        {
            source.Dispose();
        }
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

        // Auto-run: start the output first so capture does not build a permanent
        // backlog while the endpoint is still initializing.
        EnsureRunning(output);
        EnsureRunning(source);
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
        InvalidateEngine();
    }

    /// <summary>Sends a modifier's output to an output node (modifier â†’ output).</summary>
    /// <summary>Routes a key signal from a source into a modifier without mixing it audibly.</summary>
    public void ConnectSourceToModifierSidechain(SourceNode source, ModifierNode modifier)
    {
        if (!modifier.SidechainInputs.Contains(source))
        {
            modifier.SidechainInputs.Add(source);
        }

        EnsureRunning(source);
        InvalidateEngine();
    }

    public void DisconnectSourceFromModifierSidechain(SourceNode source, ModifierNode modifier)
    {
        modifier.SidechainInputs.Remove(source);
        InvalidateEngine();
    }

    /// <summary>Routes a processed modifier block into another modifier's sidechain key.</summary>
    public void ConnectModifierToModifierSidechain(ModifierNode source, ModifierNode modifier)
    {
        if (ReferenceEquals(source, modifier))
        {
            return;
        }

        if (!modifier.SidechainModifiers.Contains(source))
        {
            modifier.SidechainModifiers.Add(source);
        }

        foreach (var upstreamSource in UpstreamSources(source))
        {
            EnsureRunning(upstreamSource);
        }

        InvalidateEngine();
    }

    public void DisconnectModifierFromModifierSidechain(ModifierNode source, ModifierNode modifier)
    {
        modifier.SidechainModifiers.Remove(source);
        InvalidateEngine();
    }

    public void ConnectModifierToOutput(ModifierNode modifier, OutputNode output, float gain = 1f)
    {
        if (!modifier.Outputs.Contains(output))
        {
            modifier.Outputs.Add(output);
        }

        modifier.OutputGains[output] = gain;

        // Ensure a route exists for every source that reaches this modifier,
        // including sources entering through upstream modifier chains.
        foreach (var source in UpstreamSources(modifier))
        {
            EnsureRunning(source);
        }

        SyncOutputRoutes(output);
        EnsureRunning(output);
        InvalidateEngine();
    }

    /// <summary>Updates the send gain for a modifier-to-output route.</summary>
    public void SetModifierOutputGain(ModifierNode modifier, OutputNode output, float gain)
    {
        modifier.OutputGains[output] = gain;
        _engine?.UpdateModifierOutputGain(modifier, output, gain);
    }

    /// <summary>Updates the source-to-output gain for a direct route.</summary>
    public void SetRouteGain(SourceNode source, OutputNode output, float gain)
    {
        var route = output.Inputs.FirstOrDefault(item => item.Source == source);
        if (route != null)
        {
            route.Gain = gain;
        }

        _engine?.UpdateRouteGain(source, output, gain);
    }

    /// <summary>
    /// Switches one output between the normal 48 kHz graph and a raw native
    /// source stream. Returns null on success or a user-facing reason.
    /// </summary>
    public string? SetNativePassthrough(
        OutputNode output, SourceNode source, bool enabled,
        IAudioProcessor[]? processors = null)
    {
        if (!Outputs.Contains(output) || !Sources.Contains(source))
        {
            return "The source or output is no longer in the graph.";
        }

        bool wasRunning = output.IsRunning;
        if (enabled && output.IsNativePassthrough &&
            ReferenceEquals(output.NativeSource, source))
        {
            output.SetNativeChain(source, processors ?? Array.Empty<IAudioProcessor>());
            return null;
        }

        if (enabled)
        {
            if (!output.CanNativePassthrough(source, out string reason))
            {
                return reason;
            }

            if (wasRunning)
            {
                output.Stop();
            }

            output.UseGraphEngine = false;
            output.SetNativeChain(source, processors ?? Array.Empty<IAudioProcessor>());
            InvalidateEngine();

            if (wasRunning)
            {
                if (!source.IsRunning)
                {
                    EnsureRunning(source);
                }

                try
                {
                    output.Start();
                }
                catch (Exception ex)
                {
                    output.SetNativeSource(null);
                    output.UseGraphEngine = true;
                    InvalidateEngine();
                    return "Could not start native passthrough: " + ex.Message;
                }
            }

            return null;
        }

        if (wasRunning)
        {
            output.Stop();
        }

        output.SetNativeChain(null, Array.Empty<IAudioProcessor>());
        output.UseGraphEngine = true;
        SyncOutputRoutes(output);
        InvalidateEngine();
        if (wasRunning)
        {
            try
            {
                output.Start();
            }
            catch (Exception ex)
            {
                return "Could not restart the normal route: " + ex.Message;
            }
        }

        return null;
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

        InvalidateEngine();
    }

    public void DisconnectModifierFromModifier(ModifierNode from, ModifierNode to)
    {
        from.Next.Remove(to);

        foreach (var output in ReachableOutputs(to).Concat(ReachableOutputs(from)).Distinct())
        {
            SyncOutputRoutes(output);
        }

        InvalidateEngine();
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
        modifier.OutputGains.Remove(output);
        SyncOutputRoutes(output);
        InvalidateEngine();
    }

    public void DisconnectSourceFromModifier(SourceNode source, ModifierNode modifier)
    {
        modifier.Inputs.Remove(source);

        foreach (var output in ReachableOutputs(modifier))
        {
            SyncOutputRoutes(output);
        }

        InvalidateEngine();
    }

    /// <summary>Rebuilds routes and the renderer after an in-place modifier edit.</summary>
    public void RefreshModifiers()
    {
        foreach (var output in Outputs)
        {
            RebuildRouteProcessors(output);
        }

        InvalidateEngine();
    }

    /// <summary>Rebuilds each route's private effect chain and alignment delay.</summary>
    public void RebuildRouteProcessors(OutputNode output)
    {
        if (output.UseGraphEngine)
        {
            // The graph renderer processes modifiers once and computes its own
            // latency/alignment; per-route clones would run effects twice.
            output.RefreshRoutes();
            InvalidateEngine();
            return;
        }

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
        InvalidateEngine();
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
            var processors = new List<IAudioProcessor>();
            foreach (var processor in modifier.Processors)
            {
                // Taps are fed from the modifier's post-effect block by the renderer.
                if (processor is not ITapProcessor)
                {
                    processors.Add(processor);
                }
            }

            if (modifier.Parallel && processors.Count > 0)
            {
                var parallel = modifier.ParallelChain;
                if (parallel == null)
                {
                    parallel = new ParallelChainProcessor(processors.ToArray(), modifier.Send);
                    modifier.ParallelChain = parallel;
                }
                else
                {
                    parallel.Send = modifier.Send;
                }

                chain.Add(parallel);
            }
            else
            {
                chain.AddRange(processors);
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
        _engine?.Dispose();
        _engine = null;

        foreach (var output in Outputs)
        {
            output.Dispose();
        }

        foreach (var source in Sources)
        {
            source.Dispose();
        }

        foreach (var modifier in Modifiers)
        {
            modifier.Dispose();
        }

        _directLinks.Clear();
        Outputs.Clear();
        Sources.Clear();
        Modifiers.Clear();
    }
}
