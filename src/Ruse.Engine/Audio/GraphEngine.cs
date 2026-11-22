using System.Diagnostics;
using System.Numerics;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Single real-time graph renderer. One thread evaluates every source and
/// modifier once per block and fills one ring per output; output pumps only
/// consume those rings. Taps read the modifier's post-effect block through a
/// worker, so an analyser never causes the effect chain to run twice.
/// </summary>
internal sealed class GraphEngine : IDisposable
{
    private readonly AudioGraph _graph;
    private readonly object _sync = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Action _wakeCallback;
    private Thread? _thread;
    private volatile bool _running;
    private volatile CompiledGraph? _compiled;
    private long _lastTapTick;
    private bool _disposed;
    private AudioWatchdogToken? _watchdog;

    internal int TapWorkerCount
    {
        get
        {
            lock (_sync)
            {
                return _compiled?.TapWorkers.Count ?? 0;
            }
        }
    }

    internal long TapDroppedFrames
    {
        get
        {
            var compiled = _compiled;
            if (compiled == null)
            {
                return 0;
            }

            long total = 0;
            for (int i = 0; i < compiled.TapWorkers.Count; i++)
            {
                total += compiled.TapWorkers[i].DroppedFrames;
            }

            return total;
        }
    }

    internal GraphEngine(AudioGraph graph)
    {
        _graph = graph;
        _wakeCallback = () => _wake.Set();
    }

    internal void Start()
    {
        lock (_sync)
        {
            if (_running || _disposed)
            {
                return;
            }

            _running = true;
            CompileLocked();
            _watchdog = AudioThreadWatchdog.Register("GraphEngine", TimeSpan.FromSeconds(5), OnWatchdogStall);
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "GraphEngine",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            _wake.Set();
        }
    }

    private void OnWatchdogStall()
    {
        AppLog.Error("The graph renderer stopped making progress.");
        OutputNode[] outputs;
        lock (_sync)
        {
            outputs = _graph.Outputs.ToArray();
        }

        foreach (OutputNode output in outputs)
        {
            output.NotifyStalled("The graph renderer stopped responding.");
        }
    }

    internal void RenderOnceForTest()
    {
        CompiledGraph? compiled;
        lock (_sync)
        {
            if (_compiled == null)
            {
                CompileLocked();
            }

            compiled = _compiled;
        }

        if (compiled == null || !compiled.TryAcquire())
        {
            return;
        }

        try
        {
            compiled.ForceRenderForTest();
            compiled.Render();
        }
        finally
        {
            compiled.Release();
        }
    }

    internal bool Stop()
    {
        Thread? thread;
        CompiledGraph? old;
        lock (_sync)
        {
            if (!_running && _thread == null)
            {
                return true;
            }

            _running = false;
            thread = _thread;
            _thread = null;
            old = _compiled;
            _compiled = null;
            foreach (var output in _graph.Outputs)
            {
                if (ReferenceEquals(output.EngineWake, _wakeCallback))
                {
                    output.EngineWake = null;
                }
            }

            _wake.Set();
            _watchdog?.Dispose();
            _watchdog = null;
        }

        if (old != null)
        {
            RetireCompiled(old, null);
        }

        return thread == null || thread.Join(1000);
    }

    /// <summary>Rebuilds the processing snapshot after a graph change.</summary>
    /// <param name="afterDrain">
    /// Cleanup that must wait until no renderer still holds the old snapshot.
    /// Used to dispose removed nodes only after the audio thread has let go.
    /// </param>
    internal void Invalidate(Action? afterDrain = null)
    {
        // Callers that already hold the render lock (for example a mutation
        // wrapped in RunExclusive) must compile in place.
        if (Monitor.IsEntered(_sync))
        {
            if (_running)
            {
                CompileLocked(afterDrain);
            }
            else
            {
                afterDrain?.Invoke();
            }

            return;
        }

        if (!_running)
        {
            afterDrain?.Invoke();
            return;
        }

        CompiledGraph next;
        try
        {
            next = new CompiledGraph(_graph, _wakeCallback, AudioEngineConfig.BlockFrames);
        }
        catch (Exception ex)
        {
            AppLog.Error("The graph renderer could not build the processing graph.", ex);
            RetireAfterFailedCompile(afterDrain);
            return;
        }

        CompiledGraph? old;
        lock (_sync)
        {
            if (!_running)
            {
                next.Dispose();
                afterDrain?.Invoke();
                return;
            }

            old = _compiled;
            _compiled = next;
            _wake.Set();
        }

        if (old == null)
        {
            afterDrain?.Invoke();
            return;
        }

        RetireCompiled(old, afterDrain);
    }

    private static void RetireCompiled(CompiledGraph graph, Action? afterDrain)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            graph.Dispose();
            if (afterDrain != null)
            {
                graph.Retire(afterDrain);
            }
        });
    }

    /// <summary>Serializes a graph mutation with snapshot swaps.</summary>
    internal void RetireCurrent(Action action)
    {
        CompiledGraph? current;
        lock (_sync)
        {
            current = _compiled;
        }

        if (current != null)
        {
            current.Retire(action);
        }
        else
        {
            action();
        }
    }

    internal void RunExclusive(Action action)
    {
        lock (_sync)
        {
            action();
        }
    }

    internal void UpdateModifierOutputGain(ModifierNode modifier, OutputNode output, float gain)
    {
        lock (_sync)
        {
            _compiled?.UpdateModifierOutputGain(modifier, output, gain);
        }
    }

    internal void UpdateRouteGain(SourceNode source, OutputNode output, float gain)
    {
        lock (_sync)
        {
            _compiled?.UpdateRouteGain(source, output, gain);
        }
    }

    internal void UpdateMixerInput(ModifierNode mixer, SourceNode? source, ModifierNode? upstream,
        ModifierNode.MixerInputSettings settings)
    {
        lock (_sync)
        {
            _compiled?.UpdateMixerInput(mixer, source, upstream, settings);
        }
    }

    private void CompileLocked(Action? afterDrain = null)
    {
        try
        {
            var next = new CompiledGraph(_graph, _wakeCallback, AudioEngineConfig.BlockFrames);
            var old = _compiled;
            _compiled = next;
            if (old == null)
            {
                afterDrain?.Invoke();
            }
            else
            {
                // Disposing the old graph may join tap workers. Never do that
                // while the UI thread holds _sync; hand it to the pool.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    old.Dispose();
                    if (afterDrain != null)
                    {
                        old.Retire(afterDrain);
                    }
                });
            }

            _wake.Set();
        }
        catch (Exception ex)
        {
            // Keep the previous graph running rather than silencing the app on
            // a transient compile error (for example a device removed mid-edit).
            AppLog.Error("The graph renderer could not build the processing graph.", ex);
            RetireAfterFailedCompile(afterDrain);
        }
    }

    private void RetireAfterFailedCompile(Action? afterDrain)
    {
        if (afterDrain == null)
        {
            return;
        }

        CompiledGraph? current;
        lock (_sync)
        {
            current = _compiled;
        }

        if (current != null)
        {
            current.Retire(afterDrain);
        }
        else
        {
            afterDrain();
        }
    }

    private void Loop()
    {
        TimeBeginPeriod(1);
        using var mmcss = MmcssScope.Enter(priority: AvrtPriority.Critical);
        try
        {
            while (_running)
            {
                _watchdog?.Beat();
                bool rendered = false;
                int waitMs = 20;
                CompiledGraph? compiled = _compiled;
                if (!_running)
                {
                    break;
                }

                if (compiled == null)
                {
                    _wake.WaitOne(20);
                    continue;
                }

                if (!compiled.TryAcquire())
                {
                    Thread.Yield();
                    continue;
                }

                try
                {
                    if (compiled.HasRunningOutputs)
                    {
                        int guard = 0;
                        while (_running && compiled.NeedsData && guard++ < 8)
                        {
                            compiled.Render();
                            rendered = true;
                        }
                    }
                    else if (compiled.HasTapWorkers)
                    {
                        waitMs = 5;
                        long now = Environment.TickCount64;
                        if (now - _lastTapTick >= 5)
                        {
                            compiled.Render();
                            _lastTapTick = now;
                            rendered = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AudioFaultLog.ReportGraphFault(ex);
                }
                finally
                {
                    compiled.Release();
                }

                if (rendered)
                {
                    continue;
                }

                _wake.WaitOne(waitMs);
            }
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Stop())
        {
            _wake.Dispose();
        }
        else
        {
            // The render thread may still be inside WaitOne. Disposing the
            // event now would turn a slow stop into an ObjectDisposedException
            // on the audio thread, so keep it alive for this process.
            AppLog.Warning("Graph renderer did not stop in time; leaving its wake event undisposed.");
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    private sealed class CompiledGraph : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private readonly AudioGraph _audioGraph;

        private static int UserDelayFrames(float delayMs) =>
            float.IsFinite(delayMs)
                ? (int)MathF.Round(Math.Clamp(delayMs, 0f, ModifierNode.MixerMaxDelayMs) * SampleRate / 1000f)
                : 0;
        private readonly int _blockFrames;
        private readonly float[] _scratchTemp;
        private readonly float[] _scratchSidechain;
        private readonly float[] _scratchDry;
        private readonly float[] _scratchInput;
        private readonly float[] _scratchMix;

        public List<SourceState> Sources { get; } = new();
        public List<NodeState> Nodes { get; } = new();
        public List<OutputState> Outputs { get; } = new();
        public List<TapWorker> TapWorkers { get; } = new();
        public bool HasNative { get; private set; }
        private readonly object _leaseGate = new();
        private readonly List<Action> _retireActions = new();
        private int _leases;
        private int _disposeRequested;
        private int _disposed;
        private bool _forceRenderForTest;

        internal void ForceRenderForTest() => _forceRenderForTest = true;

        /// <summary>Claims the snapshot for one render pass.</summary>
        public bool TryAcquire()
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                return false;
            }

            Interlocked.Increment(ref _leases);
            if (Volatile.Read(ref _disposeRequested) == 0)
            {
                return true;
            }

            Release();
            return false;
        }

        public void Release()
        {
            int leases = Interlocked.Decrement(ref _leases);
            if (leases == 0 &&
                Volatile.Read(ref _disposeRequested) != 0 &&
                Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // The last lease is normally released on the render thread.
                // Disposing tap workers joins them, so move teardown off the
                // audio path instead of stalling the callback.
                ThreadPool.QueueUserWorkItem(
                    static state => ((CompiledGraph)state!).DisposeCore(), this);
            }
        }

        /// <summary>Schedules cleanup that may only run after the last lease ends.</summary>
        public void Retire(Action action)
        {
            lock (_leaseGate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _retireActions.Add(action);
                    return;
                }
            }

            action();
        }

        public void UpdateModifierOutputGain(ModifierNode modifier, OutputNode output, float gain)
        {
            for (int i = 0; i < Outputs.Count; i++)
            {
                var state = Outputs[i];
                if (!ReferenceEquals(state.Output, output))
                {
                    continue;
                }

                for (int j = 0; j < state.Contributions.Count; j++)
                {
                    var contribution = state.Contributions[j];
                    if (contribution.Modifier?.IsFor(modifier) == true)
                    {
                        contribution.Gain = gain;
                    }
                }
            }
        }

        public void UpdateRouteGain(SourceNode source, OutputNode output, float gain)
        {
            for (int i = 0; i < Outputs.Count; i++)
            {
                var state = Outputs[i];
                if (!ReferenceEquals(state.Output, output))
                {
                    continue;
                }

                for (int j = 0; j < state.Contributions.Count; j++)
                {
                    var contribution = state.Contributions[j];
                    if (contribution.Source?.IsFor(source) == true)
                    {
                        contribution.Gain = gain;
                    }
                }
            }
        }

        public void UpdateMixerInput(ModifierNode mixer, SourceNode? source, ModifierNode? upstream,
            ModifierNode.MixerInputSettings settings)
        {
            NodeState? node = null;
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (ReferenceEquals(Nodes[i].Modifier, mixer))
                {
                    node = Nodes[i];
                    break;
                }
            }

            if (node == null)
            {
                return;
            }

            for (int i = 0; i < node.Inputs.Count; i++)
            {
                var input = node.Inputs[i];
                bool matches = source != null
                    ? input.Source?.IsFor(source) == true
                    : input.Upstream != null && ReferenceEquals(input.Upstream.Modifier, upstream);
                if (!matches)
                {
                    continue;
                }

                input.LinkGain = settings.Gain;
                input.Pan = settings.Pan;
                input.DelayMs = settings.DelayMs;
                input.Muted = settings.Muted;
                input.Solo = settings.Solo;
                input.BusB = settings.BusB;
                input.Inverted = settings.Inverted;
                input.MixerDelay?.SetDelayFrames(UserDelayFrames(settings.DelayMs));
                return;
            }
        }

        public bool HasTapWorkers => TapWorkers.Count > 0;

        public bool HasRunningOutputs
        {
            get
            {
                for (int i = 0; i < Outputs.Count; i++)
                {
                    if (Outputs[i].Output.IsRunning)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private int TargetBufferFrames => Math.Max(_blockFrames, AudioEngineConfig.EngineCushionFrames);

        private int OutputTargetFrames(OutputNode output)
        {
            if (!AudioEngineConfig.ElasticRing)
            {
                return TargetBufferFrames;
            }

            int max = TargetBufferFrames;
            int target = output.ElasticTargetFrames <= 0 ? _blockFrames : output.ElasticTargetFrames;
            target = Math.Clamp(target, _blockFrames, max);
            long now = Environment.TickCount64;

            if (output.Underruns > output.ElasticUnderrunMark)
            {
                output.ElasticUnderrunMark = output.Underruns;
                target = Math.Min(max, target + _blockFrames);
                output.ElasticLastAdjust = now;
            }
            else if (target > _blockFrames && now - output.ElasticLastAdjust >= 2000)
            {
                target = Math.Max(_blockFrames, target - _blockFrames);
                output.ElasticLastAdjust = now;
            }

            output.ElasticTargetFrames = target;
            return target;
        }

        public bool NeedsData
        {
            get
            {
                for (int i = 0; i < Outputs.Count; i++)
                {
                    var output = Outputs[i];
                    // Keep a small ring ahead of the device. Do not count the
                    // device buffer here: at startup the pump may have filled it
                    // with pre-engine silence, which would otherwise stop the
                    // renderer from ever producing real audio.
                    if (output.Output.IsRunning &&
                        output.Output.EngineBufferedFrames < OutputTargetFrames(output.Output))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public CompiledGraph(AudioGraph graph, Action wake, int blockFrames)
        {
            _audioGraph = graph;
            _blockFrames = Math.Max(1, blockFrames);
            int scratchFloats = _blockFrames * Channels;
            _scratchTemp = new float[scratchFloats];
            _scratchSidechain = new float[scratchFloats];
            _scratchDry = new float[scratchFloats];
            _scratchInput = new float[scratchFloats];
            _scratchMix = new float[scratchFloats];
            var engineOutputs = graph.Outputs.Where(output => output.UseGraphEngine).ToList();

            var seeds = new HashSet<ModifierNode>();
            foreach (var modifier in graph.Modifiers)
            {
                if (HasTap(modifier) || modifier.Outputs.Any(engineOutputs.Contains))
                {
                    seeds.Add(modifier);
                }
            }

            // Pull in every modifier upstream of a tap or an output.
            bool changed;
            do
            {
                changed = false;
                foreach (var modifier in graph.Modifiers)
                {
                    if (seeds.Contains(modifier))
                    {
                        continue;
                    }

                    if (modifier.Next.Any(seeds.Contains) ||
                        graph.Modifiers.Any(candidate =>
                            seeds.Contains(candidate) && candidate.SidechainModifiers.Contains(modifier)))
                    {
                        seeds.Add(modifier);
                        changed = true;
                    }
                }
            }
            while (changed);

            var order = new List<ModifierNode>();
            var visitState = new Dictionary<ModifierNode, int>();
            void Visit(ModifierNode modifier)
            {
                visitState.TryGetValue(modifier, out int state);
                if (state != 0)
                {
                    return;
                }

                visitState[modifier] = 1;
                foreach (var upstream in graph.Modifiers)
                {
                    if (seeds.Contains(upstream) &&
                        (upstream.Next.Contains(modifier) || modifier.SidechainModifiers.Contains(upstream)))
                    {
                        Visit(upstream);
                    }
                }

                visitState[modifier] = 2;
                order.Add(modifier);
            }

            foreach (var modifier in graph.Modifiers)
            {
                if (seeds.Contains(modifier))
                {
                    Visit(modifier);
                }
            }

            var sourceNodes = new HashSet<SourceNode>();
            foreach (var modifier in order)
            {
                foreach (var source in modifier.Inputs)
                {
                    sourceNodes.Add(source);
                }

                foreach (var source in modifier.SidechainInputs)
                {
                    sourceNodes.Add(source);
                }
            }

            foreach (var output in engineOutputs)
            {
                foreach (var source in graph.DirectSources(output))
                {
                    sourceNodes.Add(source);
                }
            }

            var sourceStates = new Dictionary<SourceNode, SourceState>();
            foreach (var source in sourceNodes)
            {
                var state = new SourceState(source, _blockFrames);
                sourceStates[source] = state;
                Sources.Add(state);
            }

            var nodeStates = new Dictionary<ModifierNode, NodeState>();
            foreach (var modifier in order)
            {
                var node = new NodeState(modifier, _blockFrames, _scratchTemp, _scratchSidechain, _scratchDry, _scratchInput);
                int maxUserDelayFrames = 0;
                int maxUserDelayCapacity = ModifierNode.MixerMaxDelayMs * SampleRate / 1000;
                foreach (var source in modifier.Inputs)
                {
                    modifier.MixerSourceInputs.TryGetValue(source, out var settings);
                    int userDelayFrames = UserDelayFrames(settings?.DelayMs ?? 0f);
                    maxUserDelayFrames = Math.Max(maxUserDelayFrames, userDelayFrames);
                    var userDelay = modifier.IsMixer
                        ? new VariableDelayLine(maxUserDelayCapacity, Channels)
                        : null;
                    userDelay?.SetDelayFrames(userDelayFrames);
                    node.Inputs.Add(new NodeInput
                    {
                        Source = sourceStates[source],
                        LinkGain = settings?.Gain ?? 1f,
                        Pan = settings?.Pan ?? 0f,
                        DelayMs = settings?.DelayMs ?? 0f,
                        Muted = settings?.Muted ?? false,
                        Solo = settings?.Solo ?? false,
                        BusB = settings?.BusB ?? false,
                        Inverted = settings?.Inverted ?? false,
                        MixerDelay = userDelay,
                    });
                }

                foreach (var upstream in graph.Modifiers)
                {
                    if (seeds.Contains(upstream) && upstream.Next.Contains(modifier) &&
                        nodeStates.TryGetValue(upstream, out var upstreamState))
                    {
                        modifier.MixerModifierInputs.TryGetValue(upstream, out var settings);
                        int userDelayFrames = UserDelayFrames(settings?.DelayMs ?? 0f);
                        maxUserDelayFrames = Math.Max(maxUserDelayFrames, userDelayFrames);
                        var userDelay = modifier.IsMixer
                            ? new VariableDelayLine(maxUserDelayCapacity, Channels)
                            : null;
                        userDelay?.SetDelayFrames(userDelayFrames);
                        node.Inputs.Add(new NodeInput
                        {
                            Upstream = upstreamState,
                            LinkGain = settings?.Gain ?? 1f,
                            Pan = settings?.Pan ?? 0f,
                            DelayMs = settings?.DelayMs ?? 0f,
                            Muted = settings?.Muted ?? false,
                            Solo = settings?.Solo ?? false,
                            BusB = settings?.BusB ?? false,
                            Inverted = settings?.Inverted ?? false,
                            MixerDelay = userDelay,
                        });
                    }
                }

                int maxInputLatency = 0;
                foreach (var input in node.Inputs)
                {
                    maxInputLatency = Math.Max(maxInputLatency, input.Latency);
                }

                foreach (var input in node.Inputs)
                {
                    int delay = maxInputLatency - input.Latency;
                    if (delay > 0)
                    {
                        input.Delay = new DelayLine(delay, Channels);
                    }
                }

                foreach (var source in modifier.SidechainInputs)
                {
                    if (!sourceStates.TryGetValue(source, out var keyState))
                    {
                        continue;
                    }

                    node.SidechainKeys.Add(new NodeInput
                    {
                        Source = keyState,
                        Delay = maxInputLatency > 0 ? new DelayLine(maxInputLatency, Channels) : null,
                    });
                }

                foreach (var keyModifier in modifier.SidechainModifiers)
                {
                    if (!nodeStates.TryGetValue(keyModifier, out var keyState))
                    {
                        continue;
                    }

                    int delay = maxInputLatency - keyState.Latency;
                    node.SidechainKeys.Add(new NodeInput
                    {
                        Upstream = keyState,
                        Delay = delay > 0 ? new DelayLine(delay, Channels) : null,
                    });
                }

                var chain = new List<IAudioProcessor>();
                bool nodeHasNative = false;
                foreach (var processor in modifier.Processors)
                {
                    if (processor is ITapProcessor)
                    {
                        continue;
                    }

                    chain.Add(processor);
                    if (processor is ISharedProcessor)
                    {
                        nodeHasNative = true;
                    }

                    if (processor is ChainProcessor nested && HasNativeInChain(nested))
                    {
                        nodeHasNative = true;
                    }
                }

                if (modifier.Parallel && chain.Count > 0)
                {
                    var parallel = modifier.ParallelChain;
                    if (parallel == null)
                    {
                        parallel = new ParallelChainProcessor(chain.ToArray(), modifier.Send);
                        modifier.ParallelChain = parallel;
                    }
                    else
                    {
                        parallel.Send = modifier.Send;
                    }

                    chain.Clear();
                    chain.Add(parallel);
                }

                node.HasNative = nodeHasNative;
                if (nodeHasNative)
                {
                    HasNative = true;
                }

                node.Chain = chain.ToArray();
                node.HasSidechain = node.Chain.Any(processor => processor is ISidechainProcessor);
                int chainLatency = 0;
                for (int i = 0; i < node.Chain.Length; i++)
                {
                    chainLatency += node.Chain[i].LatencySamples;
                }

                node.ChainLatency = chainLatency;
                node.BypassDelay = chainLatency > 0 ? new DelayLine(chainLatency, Channels) : null;
                node.Latency = maxInputLatency + maxUserDelayFrames + chainLatency;
                nodeStates[modifier] = node;
                Nodes.Add(node);

                if (HasTap(modifier))
                {
                    node.TapWorker = new TapWorker(modifier);
                    TapWorkers.Add(node.TapWorker);
                }
            }

            foreach (var output in engineOutputs)
            {
                var state = new OutputState(output, _blockFrames, _scratchMix, _scratchTemp);
                foreach (var source in graph.DirectSources(output))
                {
                    if (!sourceStates.TryGetValue(source, out var sourceState))
                    {
                        continue;
                    }

                    float gain = output.Inputs.FirstOrDefault(route => route.Source == source)?.Gain ?? 1f;
                    state.Contributions.Add(new OutputContribution
                    {
                        Source = sourceState,
                        Gain = gain,
                    });
                }

                foreach (var modifier in order)
                {
                    if (!modifier.Outputs.Contains(output) || !nodeStates.TryGetValue(modifier, out var node))
                    {
                        continue;
                    }

                    state.Contributions.Add(new OutputContribution
                    {
                        Modifier = node,
                        Gain = modifier.OutputGains.TryGetValue(output, out float sendGain) ? sendGain : 1f,
                    });
                }

                int maxLatency = 0;
                foreach (var contribution in state.Contributions)
                {
                    maxLatency = Math.Max(maxLatency, contribution.Latency);
                }

                foreach (var contribution in state.Contributions)
                {
                    int delay = maxLatency - contribution.Latency;
                    if (delay > 0)
                    {
                        contribution.Delay = new DelayLine(delay, Channels);
                    }
                }

                bool outputHasNative = state.Contributions.Any(contribution =>
                    contribution.Modifier?.HasNative == true);
                output.SetCompiledState(
                    maxLatency * 1000.0 / SampleRate, outputHasNative);
                output.EngineWake = wake;

                if (AudioEngineConfig.ElasticRing)
                {
                    if (output.ElasticVersion != AudioEngineConfig.Version ||
                        output.ElasticTargetFrames <= 0)
                    {
                        output.ElasticVersion = AudioEngineConfig.Version;
                        output.ElasticTargetFrames = _blockFrames;
                        output.ElasticUnderrunMark = output.Underruns;
                        output.ElasticLastAdjust = Environment.TickCount64;
                    }
                }
                else
                {
                    output.ElasticTargetFrames = 0;
                }

                Outputs.Add(state);
            }
        }

        private static bool HasNativeInChain(ChainProcessor chain)
        {
            foreach (var processor in chain.Processors)
            {
                if (processor is ISharedProcessor)
                {
                    return true;
                }

                if (processor is ChainProcessor nested && HasNativeInChain(nested))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddInto(Span<float> destination, ReadOnlySpan<float> source)
        {
            int i = 0;
            int width = Vector<float>.Count;
            for (; i <= destination.Length - width; i += width)
            {
                var left = new Vector<float>(destination.Slice(i));
                var right = new Vector<float>(source.Slice(i));
                (left + right).CopyTo(destination.Slice(i));
            }

            for (; i < destination.Length; i++)
            {
                destination[i] += source[i];
            }
        }

        private static void ScaleInto(Span<float> destination, float gain)
        {
            int i = 0;
            int width = Vector<float>.Count;
            var gainVector = new Vector<float>(gain);
            for (; i <= destination.Length - width; i += width)
            {
                var block = new Vector<float>(destination.Slice(i));
                (block * gainVector).CopyTo(destination.Slice(i));
            }

            for (; i < destination.Length; i++)
            {
                destination[i] *= gain;
            }
        }

        private static void CopyScaled(ReadOnlySpan<float> source, Span<float> destination, float gain)
        {
            int i = 0;
            int width = Vector<float>.Count;
            var gainVector = new Vector<float>(gain);
            for (; i <= destination.Length - width; i += width)
            {
                var block = new Vector<float>(source.Slice(i));
                (block * gainVector).CopyTo(destination.Slice(i));
            }

            for (; i < destination.Length; i++)
            {
                destination[i] = source[i] * gain;
            }
        }

        private static void AddScaled(ReadOnlySpan<float> source, Span<float> destination, float gain)
        {
            int i = 0;
            int width = Vector<float>.Count;
            var gainVector = new Vector<float>(gain);
            for (; i <= destination.Length - width; i += width)
            {
                var block = new Vector<float>(source.Slice(i));
                var target = new Vector<float>(destination.Slice(i));
                (target + block * gainVector).CopyTo(destination.Slice(i));
            }

            for (; i < destination.Length; i++)
            {
                destination[i] += source[i] * gain;
            }
        }

        private static bool HasTap(ModifierNode modifier) =>
            modifier.Processors.Any(processor => processor is ITapProcessor);

        public void Render()
        {
            _audioGraph.Timeline?.RenderBlock(_blockFrames);
            bool groupSoloSource = MixerGroupBus.AnySourceSolo;
            bool anySoloSource = false;
            for (int i = 0; i < Sources.Count; i++)
            {
                if (Sources[i].Source.Solo ||
                    (groupSoloSource && MixerGroupBus.Solo(Sources[i].Source.GroupKey)))
                {
                    anySoloSource = true;
                    break;
                }
            }

            for (int i = 0; i < Sources.Count; i++)
            {
                Sources[i].Read(anySoloSource);
            }

            for (int i = 0; i < Nodes.Count; i++)
            {
                Nodes[i].Process();
            }

            bool groupSoloOutput = MixerGroupBus.AnyOutputSolo;
            bool anySoloOutput = false;
            for (int i = 0; i < Outputs.Count; i++)
            {
                if (Outputs[i].Output.Solo ||
                    (groupSoloOutput && MixerGroupBus.Solo(Outputs[i].Output.GroupKey)))
                {
                    anySoloOutput = true;
                    break;
                }
            }

            for (int i = 0; i < Outputs.Count; i++)
            {
                if (Outputs[i].Output.IsRunning || _forceRenderForTest)
                {
                    Outputs[i].Render(anySoloOutput);
                }
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposeRequested, 1);
            if (Volatile.Read(ref _leases) == 0 &&
                Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeCore();
            }
        }

        private void DisposeCore()
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                Sources[i].Dispose();
            }

            for (int i = 0; i < TapWorkers.Count; i++)
            {
                TapWorkers[i].Dispose();
            }

            Action[] actions;
            lock (_leaseGate)
            {
                actions = _retireActions.ToArray();
                _retireActions.Clear();
            }

            for (int i = 0; i < actions.Length; i++)
            {
                actions[i]();
            }
        }

        public sealed class SourceState : IDisposable
        {
            private readonly SourceNode _source;
            private readonly SourceNode.Subscription _subscription;
            private readonly float[] _block;

            public SourceState(SourceNode source, int blockFrames)
            {
                _source = source;
                _subscription = source.Subscribe();
                _block = new float[Math.Max(1, blockFrames) * Channels];
            }

            public SourceNode Source => _source;
            public float[] Block => _block;

            public bool IsFor(SourceNode source) => ReferenceEquals(_source, source);

            public void Read(bool anySoloSource)
            {
                int read = _subscription.Read(_block);
                if (read < _block.Length)
                {
                    Array.Clear(_block, read, _block.Length - read);
                }

                if (anySoloSource && !_source.Solo &&
                    (!MixerGroupBus.AnySourceSolo || !MixerGroupBus.Solo(_source.GroupKey)))
                {
                    Array.Clear(_block);
                }
            }

            public void Dispose() => _subscription.Dispose();
        }

        public sealed class NodeInput
        {
            public SourceState? Source { get; init; }
            public NodeState? Upstream { get; init; }
            public DelayLine? Delay { get; set; }
            public float LinkGain { get; set; } = 1f;
            public float Pan { get; set; }
            public float DelayMs { get; set; }
            public bool Muted { get; set; }
            public bool Solo { get; set; }
            public bool BusB { get; set; }
            public bool Inverted { get; set; }
            public VariableDelayLine? MixerDelay { get; init; }
            public float AppliedGain { get; set; } = float.NaN;

            public int Latency => Source != null ? 0 : Upstream?.Latency ?? 0;
        }

        public sealed class NodeState
        {
            private readonly ModifierNode _modifier;
            private readonly int _blockFrames;
            private readonly float[] _block;
            private readonly float[] _temp;
            private readonly float[] _sidechain;
            private readonly float[] _dry;
            private readonly float[] _input;
            private float _wet;
            private bool _chainFaulted;
            private bool _nonFiniteLogged;

            public ModifierNode Modifier => _modifier;

            public NodeState(
                ModifierNode modifier,
                int blockFrames,
                float[] temp,
                float[] sidechain,
                float[] dry,
                float[] input)
            {
                _modifier = modifier;
                _blockFrames = Math.Max(1, blockFrames);
                int floats = _blockFrames * Channels;
                _block = new float[floats];
                _temp = temp;
                _sidechain = sidechain;
                _dry = dry;
                _input = input;
                _wet = modifier.Enabled ? 1f : 0f;
                modifier.ClearChainFault();
            }

            public List<NodeInput> Inputs { get; } = new();
            public List<NodeInput> SidechainKeys { get; } = new();
            public bool IsFor(ModifierNode modifier) => ReferenceEquals(_modifier, modifier);
            public IAudioProcessor[] Chain { get; set; } = Array.Empty<IAudioProcessor>();
            public int Latency { get; set; }
            public int ChainLatency { get; set; }
            public DelayLine? BypassDelay { get; set; }
            public bool HasNative { get; set; }
            public TapWorker? TapWorker { get; set; }
            public bool HasSidechain { get; set; }
            public float[] Block => _block;

            public void Process()
            {
                bool firstInput = true;
                bool mixer = _modifier.IsMixer;
                bool anySolo = false;
                if (mixer)
                {
                    for (int i = 0; i < Inputs.Count; i++)
                    {
                        if (Inputs[i].Solo)
                        {
                            anySolo = true;
                            break;
                        }
                    }
                }

                float crossfade = mixer ? Math.Clamp(_modifier.MixerCrossfade, 0f, 1f) : 0f;
                float crossfadeAngle = crossfade * MathF.PI * 0.5f;
                float busAGain = MathF.Cos(crossfadeAngle);
                float busBGain = MathF.Sin(crossfadeAngle);
                int audibleInputs = 0;
                for (int i = 0; i < Inputs.Count; i++)
                {
                    var input = Inputs[i];
                    var source = input.Source;
                    ReadOnlySpan<float> block;
                    if (source != null)
                    {
                        float targetGain = source.Source.Muted || MixerGroupBus.Muted(source.Source.GroupKey)
                            ? 0f
                            : source.Source.Gain * MixerGroupBus.Gain(source.Source.GroupKey);
                        if (mixer)
                        {
                            targetGain *= input.LinkGain * (input.BusB ? busBGain : busAGain);
                            if (input.Muted || (anySolo && !input.Solo))
                            {
                                targetGain = 0f;
                            }

                            if (targetGain > 0.0001f)
                            {
                                audibleInputs++;
                            }

                            if (input.Inverted)
                            {
                                targetGain = -targetGain;
                            }
                        }

                        float panL = 1f;
                        float panR = 1f;
                        if (mixer && !input.Muted)
                        {
                            float pan = Math.Clamp(input.Pan, -1f, 1f);
                            if (pan > 0f) panL = 1f - pan;
                            else if (pan < 0f) panR = 1f + pan;
                        }

                        float applied = float.IsNaN(input.AppliedGain) ? targetGain : input.AppliedGain;
                        if (input.Delay != null)
                        {
                            source.Block.AsSpan().CopyTo(_temp);
                            input.Delay.Process(_temp, Channels);
                            block = _temp;
                        }
                        else
                        {
                            block = source.Block;
                        }

                        if (mixer && input.MixerDelay != null)
                        {
                            if (input.Delay == null)
                            {
                                block.CopyTo(_temp);
                            }

                            input.MixerDelay.Process(_temp, Channels);
                            block = _temp;
                        }

                        if (!mixer)
                        {
                            if (applied == targetGain)
                            {
                                if (targetGain == 1f)
                                {
                                    if (firstInput) block.CopyTo(_block);
                                    else Add(block);
                                }
                                else if (firstInput)
                                {
                                    CopyScaled(block, _block, targetGain);
                                }
                                else
                                {
                                    AddScaled(block, _block, targetGain);
                                }
                            }
                            else
                            {
                                float step = (targetGain - applied) / _blockFrames;
                                for (int frame = 0; frame < _blockFrames; frame++)
                                {
                                    applied += step;
                                    int index = frame * Channels;
                                    if (firstInput)
                                    {
                                        _block[index] = block[index] * applied;
                                        _block[index + 1] = block[index + 1] * applied;
                                    }
                                    else
                                    {
                                        _block[index] += block[index] * applied;
                                        _block[index + 1] += block[index + 1] * applied;
                                    }
                                }
                            }
                        }
                        else if (applied == targetGain)
                        {
                            for (int frame = 0; frame < _blockFrames; frame++)
                            {
                                int index = frame * Channels;
                                float left = block[index] * targetGain * panL;
                                float right = block[index + 1] * targetGain * panR;
                                if (firstInput)
                                {
                                    _block[index] = left;
                                    _block[index + 1] = right;
                                }
                                else
                                {
                                    _block[index] += left;
                                    _block[index + 1] += right;
                                }
                            }
                        }
                        else
                        {
                            float step = (targetGain - applied) / _blockFrames;
                            for (int frame = 0; frame < _blockFrames; frame++)
                            {
                                applied += step;
                                int index = frame * Channels;
                                float left = block[index] * applied * panL;
                                float right = block[index + 1] * applied * panR;
                                if (firstInput)
                                {
                                    _block[index] = left;
                                    _block[index + 1] = right;
                                }
                                else
                                {
                                    _block[index] += left;
                                    _block[index + 1] += right;
                                }
                            }
                        }

                        firstInput = false;
                        input.AppliedGain = targetGain;
                    }
                    else if (input.Upstream is { } upstream)
                    {
                        if (input.Delay != null)
                        {
                            upstream.Block.AsSpan().CopyTo(_temp);
                            input.Delay.Process(_temp, Channels);
                            block = _temp;
                        }
                        else
                        {
                            block = upstream.Block;
                        }

                        if (mixer && input.MixerDelay != null)
                        {
                            if (input.Delay == null)
                            {
                                block.CopyTo(_temp);
                            }

                            input.MixerDelay.Process(_temp, Channels);
                            block = _temp;
                        }

                        if (!mixer)
                        {
                            if (firstInput) block.CopyTo(_block);
                            else Add(block);
                        }
                        else
                        {
                            float targetGain = input.LinkGain * (input.BusB ? busBGain : busAGain);
                            if (input.Muted || (anySolo && !input.Solo))
                            {
                                targetGain = 0f;
                            }

                            if (targetGain > 0.0001f)
                            {
                                audibleInputs++;
                            }

                            if (input.Inverted)
                            {
                                targetGain = -targetGain;
                            }

                            float panL = 1f;
                            float panR = 1f;
                            float pan = Math.Clamp(input.Pan, -1f, 1f);
                            if (pan > 0f) panL = 1f - pan;
                            else if (pan < 0f) panR = 1f + pan;
                            for (int frame = 0; frame < _blockFrames; frame++)
                            {
                                int index = frame * Channels;
                                float left = block[index] * targetGain * panL;
                                float right = block[index + 1] * targetGain * panR;
                                if (firstInput)
                                {
                                    _block[index] = left;
                                    _block[index + 1] = right;
                                }
                                else
                                {
                                    _block[index] += left;
                                    _block[index + 1] += right;
                                }
                            }
                        }

                        firstInput = false;
                    }
                }

                if (firstInput)
                {
                    Array.Clear(_block);
                }

                bool hasChainLatency = ChainLatency > 0;
                if (hasChainLatency)
                {
                    Array.Copy(_block, _dry, _block.Length);
                    BypassDelay?.Process(_dry, Channels);
                }

                float targetWet = _modifier.Enabled ? 1f : 0f;
                bool fade = Math.Abs(_wet - targetWet) > 1e-6f;
                if (targetWet == 0f && !fade)
                {
                    if (hasChainLatency)
                    {
                        Array.Copy(_dry, _block, _block.Length);
                    }

                    if (mixer)
                    {
                        _modifier.UpdateMixerMeter(_block, _blockFrames);
                    }

                    return;
                }

                if (fade && !hasChainLatency)
                {
                    Array.Copy(_block, _dry, _block.Length);
                }

                if (SidechainKeys.Count > 0 || HasSidechain)
                {
                    Array.Clear(_sidechain);
                }

                for (int i = 0; i < SidechainKeys.Count; i++)
                {
                    var key = SidechainKeys[i];
                    var keyBlock = key.Source?.Block ?? key.Upstream?.Block;
                    if (keyBlock == null)
                    {
                        continue;
                    }

                    if (key.Delay != null)
                    {
                        keyBlock.AsSpan().CopyTo(_temp);
                        key.Delay.Process(_temp, Channels);
                        for (int s = 0; s < _sidechain.Length; s++)
                        {
                            _sidechain[s] += _temp[s];
                        }
                    }
                    else
                    {
                        for (int s = 0; s < _sidechain.Length; s++)
                        {
                            _sidechain[s] += keyBlock[s];
                        }
                    }
                }

                if (_chainFaulted)
                {
                    if (hasChainLatency)
                    {
                        Array.Copy(_dry, _block, _block.Length);
                    }
                }
                else
                {
                    if (Chain.Length > 0)
                    {
                        Array.Copy(_block, _input, _block.Length);
                    }

                    try
                    {
                        for (int i = 0; i < Chain.Length; i++)
                        {
                            if (Chain[i] is ISidechainProcessor sidechain)
                            {
                                sidechain.SetSidechain(_sidechain, _blockFrames);
                            }

                            Chain[i].Process(_block, _blockFrames);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!_chainFaulted)
                        {
                            _chainFaulted = true;
                            _modifier.ReportChainFault();
                            AudioFaultLog.ReportEffectFault(_modifier.Title, ex);
                        }

                        Array.Copy(hasChainLatency ? _dry : _input, _block, _block.Length);
                    }
                }

                if (mixer)
                {
                    if (_modifier.MixerAutoGain && audibleInputs > 1)
                    {
                        float norm = 1f / MathF.Sqrt(audibleInputs);
                        for (int s = 0; s < _block.Length; s++)
                        {
                            _block[s] *= norm;
                        }
                    }

                    float width = Math.Clamp(_modifier.MixerWidth, 0f, 2f);
                    if (Math.Abs(width - 1f) > 1e-4f)
                    {
                        for (int frame = 0; frame < _blockFrames; frame++)
                        {
                            int index = frame * Channels;
                            float mid = (_block[index] + _block[index + 1]) * 0.5f;
                            float side = (_block[index] - _block[index + 1]) * 0.5f * width;
                            _block[index] = mid + side;
                            _block[index + 1] = mid - side;
                        }
                    }
                }

                bool finite = true;
                for (int s = 0; s < _block.Length; s++)
                {
                    if (!float.IsFinite(_block[s]))
                    {
                        finite = false;
                        break;
                    }
                }

                if (!finite)
                {
                    // A recursive effect that hits NaN would otherwise stay
                    // poisoned forever. Reset the chain and emit one silent block.
                    for (int i = 0; i < Chain.Length; i++)
                    {
                        Chain[i].Reset();
                    }

                    Array.Clear(_block);
                    if (!_nonFiniteLogged)
                    {
                        _nonFiniteLogged = true;
                        AudioFaultLog.ReportEffectWarning(_modifier.Title);
                    }
                }

                if (fade)
                {
                    float step = (targetWet - _wet) / _blockFrames;
                    for (int frame = 0; frame < _blockFrames; frame++)
                    {
                        _wet += step;
                        int index = frame * Channels;
                        _block[index] = _dry[index] + (_block[index] - _dry[index]) * _wet;
                        _block[index + 1] = _dry[index + 1] + (_block[index + 1] - _dry[index + 1]) * _wet;
                    }

                    _wet = targetWet;
                }

                if (mixer)
                {
                    _modifier.UpdateMixerMeter(_block, _blockFrames);
                }

                TapWorker?.Feed(_block);
            }

            private void Add(ReadOnlySpan<float> source) => AddInto(_block, source);
        }

        public sealed class OutputContribution
        {
            public SourceState? Source { get; init; }
            public NodeState? Modifier { get; init; }
            public float Gain { get; set; }
            public DelayLine? Delay { get; set; }
            public float AppliedGain { get; set; } = float.NaN;

            public int Latency => Source != null ? 0 : Modifier?.Latency ?? 0;
        }

        public sealed class OutputState
        {
            private readonly OutputNode _output;
            private readonly int _blockFrames;
            private readonly float[] _mix;
            private readonly float[] _temp;
            private float _volumeGain;

            public OutputState(OutputNode output, int blockFrames, float[] mix, float[] temp)
            {
                _output = output;
                _blockFrames = Math.Max(1, blockFrames);
                _mix = mix;
                _temp = temp;
                _volumeGain = MixerGroupBus.Muted(output.GroupKey)
                    ? 0f
                    : output.Volume * OutputNode.MasterGain * MixerGroupBus.Gain(output.GroupKey);
            }

            public OutputNode Output => _output;
            public List<OutputContribution> Contributions { get; } = new();

            public void Render(bool anySoloOutput)
            {
                if (anySoloOutput && !_output.Solo &&
                    (!MixerGroupBus.AnyOutputSolo || !MixerGroupBus.Solo(_output.GroupKey)))
                {
                    Array.Clear(_mix);
                    _output.SetMixResult(0f, 0f, -60f, -60f, false);
                    _output.SubmitEngineBlock(_mix);
                    return;
                }

                Array.Clear(_mix);
                for (int i = 0; i < Contributions.Count; i++)
                {
                    var contribution = Contributions[i];
                    if (contribution.Source is { } source)
                    {
                        float targetGain = source.Source.Muted || MixerGroupBus.Muted(source.Source.GroupKey)
                            ? 0f
                            : contribution.Gain * source.Source.Gain *
                              MixerGroupBus.Gain(source.Source.GroupKey);
                        float applied = float.IsNaN(contribution.AppliedGain)
                            ? targetGain
                            : contribution.AppliedGain;
                        source.Block.AsSpan().CopyTo(_temp);
                        contribution.Delay?.Process(_temp, Channels);
                        if (applied == targetGain)
                        {
                            if (targetGain == 1f)
                            {
                                Add(_temp);
                            }
                            else
                            {
                                AddScaled(_temp, _mix, targetGain);
                            }
                        }
                        else
                        {
                            float step = (targetGain - applied) / _blockFrames;
                            for (int frame = 0; frame < _blockFrames; frame++)
                            {
                                applied += step;
                                int index = frame * Channels;
                                _mix[index] += _temp[index] * applied;
                                _mix[index + 1] += _temp[index + 1] * applied;
                            }
                        }

                        contribution.AppliedGain = targetGain;
                    }
                    else if (contribution.Modifier is { } modifier)
                    {
                        float targetGain = contribution.Gain;
                        float applied = float.IsNaN(contribution.AppliedGain)
                            ? targetGain
                            : contribution.AppliedGain;
                        modifier.Block.AsSpan().CopyTo(_temp);
                        contribution.Delay?.Process(_temp, Channels);
                        if (applied == targetGain)
                        {
                            if (targetGain == 1f)
                            {
                                Add(_temp);
                            }
                            else
                            {
                                AddScaled(_temp, _mix, targetGain);
                            }
                        }
                        else
                        {
                            float step = (targetGain - applied) / _blockFrames;
                            for (int frame = 0; frame < _blockFrames; frame++)
                            {
                                applied += step;
                                int index = frame * Channels;
                                _mix[index] += _temp[index] * applied;
                                _mix[index + 1] += _temp[index + 1] * applied;
                            }
                        }

                        contribution.AppliedGain = targetGain;
                    }
                }

                bool clipped = false;
                for (int i = 0; i < _mix.Length; i++)
                {
                    if (!float.IsFinite(_mix[i]))
                    {
                        _mix[i] = 0f;
                        clipped = true;
                    }
                }

                float targetVolume = _output.Muted || OutputNode.MasterMuted ||
                    MixerGroupBus.Muted(_output.GroupKey)
                    ? 0f
                    : _output.Volume * OutputNode.MasterGain *
                      MixerGroupBus.Gain(_output.GroupKey);

                if (targetVolume != _volumeGain)
                {
                    float step = (targetVolume - _volumeGain) / _mix.Length;
                    for (int i = 0; i < _mix.Length; i++)
                    {
                        _volumeGain += step;
                        _mix[i] *= _volumeGain;
                    }

                    _volumeGain = targetVolume;
                }
                else if (targetVolume != 1f)
                {
                    ScaleInto(_mix, targetVolume);
                }

                if (OutputNode.ClipGuardEnabled)
                {
                    _output.ApplyClipGuard(_mix, _blockFrames);
                }
                else
                {
                    _output.ClipGuardActive = false;
                }

                for (int i = 0; i < _mix.Length; i++)
                {
                    float sample = _mix[i];
                    if (!float.IsFinite(sample))
                    {
                        _mix[i] = 0f;
                        clipped = true;
                    }
                    else if (sample > 1f)
                    {
                        _mix[i] = 1f;
                        clipped = true;
                    }
                    else if (sample < -1f)
                    {
                        _mix[i] = -1f;
                        clipped = true;
                    }
                }

                double sumLeft = 0;
                double sumRight = 0;
                int frames = Math.Max(1, _mix.Length / 2);
                for (int i = 0; i + 1 < _mix.Length; i += 2)
                {
                    sumLeft += _mix[i] * _mix[i];
                    sumRight += _mix[i + 1] * _mix[i + 1];
                }

                float meterLeft = Math.Min(1.0f, MathF.Sqrt((float)(sumLeft / frames)) * 3f);
                float meterRight = Math.Min(1.0f, MathF.Sqrt((float)(sumRight / frames)) * 3f);
                float dbLeft = (float)(20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumLeft / frames))));
                float dbRight = (float)(20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumRight / frames))));
                _output.SetMixResult(
                    float.IsFinite(meterLeft) ? meterLeft : 0f,
                    float.IsFinite(meterRight) ? meterRight : 0f,
                    float.IsFinite(dbLeft) ? dbLeft : -60f,
                    float.IsFinite(dbRight) ? dbRight : -60f,
                    clipped);
                _output.SubmitEngineBlock(_mix);
            }

            private void Add(float[] source) => AddInto(_mix, source);
        }
    }

    private sealed class TapWorker : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int BlockFrames = 240;
        private const int BlockFloats = BlockFrames * Channels;

        private readonly ModifierNode _modifier;
        private readonly int[] _speechTaps;
        private readonly SpectrumProcessor[] _spectra;
        private readonly SpectrogramProcessor[] _spectrograms;
        private readonly WaveformProcessor[] _waveforms;
        private readonly int[] _dbTaps;
        private readonly int[] _loudnessTaps;
        private readonly int[] _noteTaps;
        private readonly FloatRingBuffer _ring = new(SampleRate * Channels * 2);
        private long _droppedFrames;
        private int _trimRequested;
        private readonly Thread _thread;
        private volatile bool _running = true;
        private readonly AudioWatchdogToken _watchdog;

        public TapWorker(ModifierNode modifier)
        {
            _modifier = modifier;
            _speechTaps = modifier.Processors
                .OfType<TranscriptionProcessor>()
                .Select(processor => processor.TapId)
                .Concat(modifier.Processors
                    .OfType<TranslatorProcessor>()
                    .Select(processor => processor.TapId))
                .Where(id => id > 0)
                .ToArray();
            _spectra = modifier.Processors
                .OfType<SpectrumProcessor>()
                .Where(processor => processor.TapId > 0)
                .ToArray();
            _spectrograms = modifier.Processors
                .OfType<SpectrogramProcessor>()
                .Where(processor => processor.TapId > 0)
                .ToArray();
            _waveforms = modifier.Processors
                .OfType<WaveformProcessor>()
                .Where(processor => processor.TapId > 0)
                .ToArray();
            _dbTaps = modifier.Processors
                .OfType<DbMeterProcessor>()
                .Select(processor => processor.TapId)
                .Where(id => id > 0)
                .ToArray();
            _loudnessTaps = modifier.Processors
                .OfType<LoudnessProcessor>()
                .Select(processor => processor.TapId)
                .Where(id => id > 0)
                .ToArray();
            _noteTaps = modifier.Processors
                .OfType<NoteDetectorProcessor>()
                .Select(processor => processor.TapId)
                .Where(id => id > 0)
                .ToArray();

            _watchdog = AudioThreadWatchdog.Register($"TapWorker-{modifier.Title}", TimeSpan.FromSeconds(5),
                () => AppLog.Error($"Tap worker for '{modifier.Title}' stopped making progress."));
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = $"TapWorker-{modifier.Title}",
            };
            _thread.Start();
        }

        public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

        public void Feed(ReadOnlySpan<float> block)
        {
            int written = _ring.Write(block);
            if (written >= block.Length)
            {
                return;
            }

            int dropped = (block.Length - written) / Channels;
            if (dropped > 0)
            {
                Interlocked.Add(ref _droppedFrames, dropped);
                Interlocked.Exchange(ref _trimRequested, 1);
            }
        }

        private void Loop()
        {
            var block = new float[BlockFloats];
            bool faulted = false;
            while (_running)
            {
                _watchdog.Beat();
                if (Interlocked.Exchange(ref _trimRequested, 0) != 0)
                {
                    int available = _ring.AvailableToRead;
                    int keep = Math.Min(available, BlockFloats * 2);
                    if (available > keep)
                    {
                        _ring.Skip(available - keep);
                    }
                }

                int read = _ring.Read(block);
                if (read <= 0)
                {
                    Thread.Sleep(2);
                    continue;
                }

                try
                {
                    int frames = read / Channels;
                    var samples = block.AsSpan(0, frames * Channels);
                    for (int i = 0; i < _speechTaps.Length; i++)
                    {
                        TranscriptionProcessor.WriteTap(_speechTaps[i], samples, frames);
                    }

                    for (int i = 0; i < _spectra.Length; i++)
                    {
                        var spectrum = _spectra[i];
                        SpectrumProcessor.WriteTap(spectrum.TapId, samples, frames,
                            spectrum.Smoothing, spectrum.Floor, spectrum.Ceiling);
                    }

                    for (int i = 0; i < _spectrograms.Length; i++)
                    {
                        var spectrogram = _spectrograms[i];
                        SpectrogramProcessor.WriteTap(spectrogram.TapId, samples, frames,
                            spectrogram.Contrast, spectrogram.Floor, spectrogram.Ceiling);
                    }

                    for (int i = 0; i < _waveforms.Length; i++)
                    {
                        WaveformProcessor.WriteTap(_waveforms[i].TapId, samples, frames);
                    }

                    for (int i = 0; i < _dbTaps.Length; i++)
                    {
                        DbMeterProcessor.WriteTap(_dbTaps[i], samples, frames);
                    }

                    for (int i = 0; i < _loudnessTaps.Length; i++)
                    {
                        LoudnessProcessor.WriteTap(_loudnessTaps[i], samples, frames);
                    }
                    for (int i = 0; i < _noteTaps.Length; i++)
                    {
                        NoteDetectorProcessor.WriteTap(_noteTaps[i], samples, frames);
                    }
                }
                catch (Exception ex)
                {
                    if (!faulted)
                    {
                        faulted = true;
                        AppLog.Error($"Tap worker for '{_modifier.Title}' failed.", ex);
                    }
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            _watchdog.Dispose();
            if (!_thread.Join(2000))
            {
                AppLog.Warning($"Tap worker for '{_modifier.Title}' did not stop in time.");
            }
        }
    }
}
