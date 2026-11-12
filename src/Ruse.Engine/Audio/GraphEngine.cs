using System.Diagnostics;
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
    private CompiledGraph? _compiled;
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
        lock (_sync)
        {
            if (!_running && _thread == null)
            {
                return true;
            }

            _running = false;
            thread = _thread;
            _thread = null;
            _compiled?.Dispose();
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

        old.Dispose();
        if (afterDrain != null)
        {
            old.Retire(afterDrain);
        }
    }

    /// <summary>Serializes a graph mutation with snapshot swaps.</summary>
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
                old.Dispose();
                if (afterDrain != null)
                {
                    old.Retire(afterDrain);
                }
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
        using var mmcss = MmcssScope.Enter();
        try
        {
            while (_running)
            {
                _watchdog?.Beat();
                bool rendered = false;
                int waitMs = 20;
                CompiledGraph? compiled;
                lock (_sync)
                {
                    if (!_running)
                    {
                        break;
                    }

                    compiled = _compiled;
                    if (compiled == null)
                    {
                        Monitor.Wait(_sync, 20);
                        continue;
                    }
                }

                if (!compiled.TryAcquire())
                {
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
        private readonly int _blockFrames;

        public List<SourceState> Sources { get; } = new();
        public List<NodeState> Nodes { get; } = new();
        public List<OutputState> Outputs { get; } = new();
        public List<TapWorker> TapWorkers { get; } = new();
        public bool HasNative { get; private set; }
        private readonly object _leaseGate = new();
        private readonly List<Action> _retireActions = new();
        private int _leases;
        private bool _disposeRequested;
        private bool _disposed;
        private bool _forceRenderForTest;

        internal void ForceRenderForTest() => _forceRenderForTest = true;

        /// <summary>Claims the snapshot for one render pass.</summary>
        public bool TryAcquire()
        {
            lock (_leaseGate)
            {
                if (_disposeRequested)
                {
                    return false;
                }

                _leases++;
                return true;
            }
        }

        public void Release()
        {
            bool dispose = false;
            lock (_leaseGate)
            {
                if (_leases > 0)
                {
                    _leases--;
                }

                if (_leases == 0 && _disposeRequested && !_disposed)
                {
                    _disposed = true;
                    dispose = true;
                }
            }

            if (dispose)
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
                if (!_disposed)
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
                var node = new NodeState(modifier, _blockFrames);
                foreach (var source in modifier.Inputs)
                {
                    node.Inputs.Add(new NodeInput { Source = sourceStates[source] });
                }

                foreach (var upstream in graph.Modifiers)
                {
                    if (seeds.Contains(upstream) && upstream.Next.Contains(modifier) &&
                        nodeStates.TryGetValue(upstream, out var upstreamState))
                    {
                        node.Inputs.Add(new NodeInput { Upstream = upstreamState });
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
                int chainLatency = 0;
                for (int i = 0; i < node.Chain.Length; i++)
                {
                    chainLatency += node.Chain[i].LatencySamples;
                }

                node.ChainLatency = chainLatency;
                node.BypassDelay = chainLatency > 0 ? new DelayLine(chainLatency, Channels) : null;
                node.Latency = maxInputLatency + chainLatency;
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
                var state = new OutputState(output, _blockFrames);
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

        private static bool HasTap(ModifierNode modifier) =>
            modifier.Processors.Any(processor => processor is ITapProcessor);

        public void Render()
        {
            _audioGraph.Timeline?.RenderBlock(_blockFrames);
            bool anySoloSource = false;
            for (int i = 0; i < Sources.Count; i++)
            {
                if (Sources[i].Source.Solo)
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

            bool anySoloOutput = false;
            for (int i = 0; i < Outputs.Count; i++)
            {
                if (Outputs[i].Output.Solo)
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
            bool dispose = false;
            lock (_leaseGate)
            {
                _disposeRequested = true;
                if (_leases == 0 && !_disposed)
                {
                    _disposed = true;
                    dispose = true;
                }
            }

            if (dispose)
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

                if (anySoloSource && !_source.Solo)
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

            public NodeState(ModifierNode modifier, int blockFrames)
            {
                _modifier = modifier;
                _blockFrames = Math.Max(1, blockFrames);
                int floats = _blockFrames * Channels;
                _block = new float[floats];
                _temp = new float[floats];
                _sidechain = new float[floats];
                _dry = new float[floats];
                _input = new float[floats];
                _wet = modifier.Enabled ? 1f : 0f;
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
            public float[] Block => _block;

            public void Process()
            {
                bool firstInput = true;
                for (int i = 0; i < Inputs.Count; i++)
                {
                    var input = Inputs[i];
                    var source = input.Source;
                    ReadOnlySpan<float> block;
                    if (source != null)
                    {
                        float targetGain = source.Source.Muted ? 0f : source.Source.Gain;
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

                        if (applied == targetGain)
                        {
                            if (targetGain == 1f)
                            {
                                if (firstInput) block.CopyTo(_block);
                                else Add(block);
                            }
                            else
                            {
                                for (int s = 0; s < block.Length; s++)
                                {
                                    if (firstInput) _block[s] = block[s] * targetGain;
                                    else _block[s] += block[s] * targetGain;
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

                        if (firstInput) block.CopyTo(_block);
                        else Add(block);
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

                    return;
                }

                if (fade && !hasChainLatency)
                {
                    Array.Copy(_block, _dry, _block.Length);
                }

                Array.Clear(_sidechain);
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
                            AppLog.Error(
                                $"Effect chain '{_modifier.Title}' failed; passing its input through.", ex);
                        }

                        Array.Copy(hasChainLatency ? _dry : _input, _block, _block.Length);
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
                        AppLog.Warning(
                            $"Effect chain '{_modifier.Title}' produced non-finite samples; state was reset.");
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

                TapWorker?.Feed(_block);
            }

            private void Add(ReadOnlySpan<float> source)
            {
                for (int i = 0; i < _block.Length; i++)
                {
                    _block[i] += source[i];
                }
            }
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

            public OutputState(OutputNode output, int blockFrames)
            {
                _output = output;
                _blockFrames = Math.Max(1, blockFrames);
                int floats = _blockFrames * Channels;
                _mix = new float[floats];
                _temp = new float[floats];
                _volumeGain = output.Volume * OutputNode.MasterGain;
            }

            public OutputNode Output => _output;
            public List<OutputContribution> Contributions { get; } = new();

            public void Render(bool anySoloOutput)
            {
                if (anySoloOutput && !_output.Solo)
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
                        float targetGain = source.Source.Muted
                            ? 0f
                            : contribution.Gain * source.Source.Gain;
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
                                for (int s = 0; s < _temp.Length; s++)
                                {
                                    _mix[s] += _temp[s] * targetGain;
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
                                for (int s = 0; s < _temp.Length; s++)
                                {
                                    _mix[s] += _temp[s] * targetGain;
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

                float targetVolume = _output.Muted || OutputNode.MasterMuted
                    ? 0f
                    : _output.Volume * OutputNode.MasterGain;

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
                    for (int i = 0; i < _mix.Length; i++)
                    {
                        _mix[i] *= targetVolume;
                    }
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

            private void Add(float[] source)
            {
                for (int i = 0; i < _mix.Length; i++)
                {
                    _mix[i] += source[i];
                }
            }
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

        public void Feed(ReadOnlySpan<float> block)
        {
            _ring.Write(block);
        }

        private void Loop()
        {
            var block = new float[BlockFloats];
            bool faulted = false;
            while (_running)
            {
                _watchdog.Beat();
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
