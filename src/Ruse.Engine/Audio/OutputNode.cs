using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>A render endpoint with its own mixing thread.</summary>
public sealed class OutputNode : IDisposable
{
    private readonly MMDevice _device;
    private WasapiPlayer? _wasapiOut;
    private BufferedWaveProvider? _buffer;
    private Thread? _pumpThread;
    private volatile bool _running;
    private float _meterLevel;

    // ASIO path: when set, the mix is rendered to an ASIO driver instead of a
    // WASAPI endpoint. The driver owns its clock, so the rate can differ.
    private AsioOutputEndpoint? _asioEndpoint;
    private AsioDriverInfo? _asioDriver;

    // File path: when set, the mix is written to disk instead of a device.
    private AudioFileRecorder? _recorder;
    private string? _recordingPath;
    private AudioFileFormat _recordingFormat = AudioFileFormat.Wav24;
    private double _lastRecordedSeconds;
    private string? _lastRecordingError;

    /// <summary>Persistent recorder control state.</summary>
    public RecorderMode RecorderMode { get; set; } = RecorderMode.Off;
    public bool AutoSave { get; set; }

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; }
    public string Title { get; set; }
    public float Volume { get; set; } = 1.0f;
    public bool Muted { get; set; }

    /// <summary>True when this output records to a file rather than a device.</summary>
    public bool IsFileRecorder { get; private set; }

    /// <summary>True for a hidden tap monitor.</summary>
    public bool IsMonitor { get; private set; }

    /// <summary>Receives the post-effect mix before master gain for a monitor output.</summary>
    private Action<float[], int>? _mixSink;

    /// <summary>False for recorders restored from a preset.</summary>
    public bool AutoStart { get; set; } = true;

    public string RecordingPath => _recordingPath ?? string.Empty;
    public bool HasRecordingPath => !string.IsNullOrWhiteSpace(_recordingPath);
    public AudioFileFormat RecordingFormat => _recordingFormat;
    public bool IsRecording => _recorder != null;
    public double RecordedSeconds => _recorder?.Duration ?? _lastRecordedSeconds;
    public string? RecordingError => _recorder?.LastError ?? _lastRecordingError;

    /// <summary>Global output gain applied on top of per-node volume.</summary>
    public static volatile float MasterGain = 1.0f;

    /// <summary>Global mute; when true every output renders silence.</summary>
    public static volatile bool MasterMuted = false;

    public float MeterLevel => _meterLevel;
    public bool IsRunning => _running;

    /// <summary>True when the most recent mix block exceeded full scale and was clipped.</summary>
    public volatile bool Clipping;

    /// <summary>Raised when the render endpoint fails or is removed (device unplugged).</summary>
    public event Action<OutputNode, string>? Faulted;

    private volatile bool _stopRequested;

    /// <summary>Approximate endpoint latency, in ms.</summary>
    public double EstimatedLatencyMs
    {
        get
        {
            double queued = _buffer?.BufferedDuration.TotalMilliseconds ?? 0;
            return _outputLatencyMs + queued + _chainLatencyMs;
        }
    }

    /// <summary>Latency added by effect chains and alignment, in ms.</summary>
    public double ChainLatencyMs => _chainLatencyMs;

    private double _outputLatencyMs;
    private double _chainLatencyMs;

    /// <summary>True when the endpoint is actually running in IAudioClient3 low-latency mode.</summary>
    public bool LowLatencyActive => _lowLatency;

    private bool _lowLatency;

    /// <summary>Rate the ASIO driver actually runs at (0 for WASAPI outputs).</summary>
    private int _asioSampleRate;

    /// <summary>Routes (source + gain) feeding this output.</summary>
    public List<Route> Inputs { get; } = new();

    // Immutable snapshot the render thread iterates without allocating or locking.
    // Rebuilt only when routes change.
    private volatile Route[] _routeSnapshot = Array.Empty<Route>();

    /// <summary>Rebuilds the render-thread snapshot. Call after mutating <see cref="Inputs"/>.</summary>
    public void RefreshRoutes()
    {
        var routes = Inputs.ToArray();

        // Routes that are gone release their copy of the source audio.
        foreach (var old in _routeSnapshot)
        {
            if (!routes.Contains(old))
            {
                old.Dispose();
            }
        }

        _routeSnapshot = routes;

        // Take each route's copy of its source straight away, so a route hears
        // the source from the moment it is connected rather than from its
        // first render.
        for (int i = 0; i < routes.Length; i++)
        {
            _ = routes[i].Subscription;
        }

        int maxFrames = 0;
        for (int i = 0; i < routes.Length; i++)
        {
            maxFrames = Math.Max(maxFrames, routes[i].InherentLatencyFrames + routes[i].CompensationFrames);
        }

        _chainLatencyMs = maxFrames * 1000.0 / 48000;
    }

    /// <summary>Optional in-memory sink for tests; when set, no WASAPI device is used.</summary>
    private Action<float[], int>? _testSink;

    public OutputNode(MMDevice device)
    {
        _device = device;
        DeviceId = device.ID;
        Title = device.FriendlyName;
    }

    /// <summary>Creates an output that renders to an ASIO driver.</summary>
    public OutputNode(AsioDriverInfo driver)
    {
        _device = null!;
        DeviceId = AsioCatalog.IdFor(driver.Name);
        Title = driver.Name;
        _asioDriver = driver;
    }

    /// <summary>Creates a device-less output that pushes mixed frames to a sink.</summary>
    public static OutputNode CreateForTest(Action<float[], int> sink)
    {
        var node = new OutputNode();
        node._testSink = sink;
        return node;
    }

    /// <summary>Creates a silent real-time output for a tap sink.</summary>
    public static OutputNode CreateMonitor(Action<float[], int> sink)
    {
        var node = new OutputNode("monitor:" + Guid.NewGuid().ToString("N"), "Tap monitor");
        node.IsMonitor = true;
        node.PaceRealTime = true;
        node._testSink = static (_, _) => { };
        node._mixSink = sink;
        return node;
    }

    /// <summary>Creates an output that records the mix to an audio file.</summary>
    public static OutputNode CreateForFile(string path, AudioFileFormat format)
    {
        var node = new OutputNode("file:" + Guid.NewGuid().ToString("N"), "File recorder");
        node.IsFileRecorder = true;
        node._recordingPath = path;
        node._recordingFormat = format;
        return node;
    }

    private OutputNode()
        : this("test", "Test sink")
    {
    }

    private OutputNode(string deviceId, string title)
    {
        _device = null!;
        DeviceId = deviceId;
        Title = title;
    }

    /// <summary>Changes the destination while the recorder is stopped.</summary>
    public void SetRecordingDestination(string path, AudioFileFormat format)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Stop recording before changing the destination.");
        }

        _recordingPath = path;
        _recordingFormat = format;
        _lastRecordedSeconds = 0;
        _lastRecordingError = null;
        Title = System.IO.Path.GetFileName(path);
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        if (IsFileRecorder && !HasRecordingPath)
        {
            // An unarmed recorder stays idle; the node UI says to choose a file.
            return;
        }

        _stopRequested = false;
        _running = true;
        Underruns = 0;
        _renderedOnce = false;

        int sampleRate = 48000;
        int channels = 2;

        if (IsFileRecorder)
        {
            _lastRecordedSeconds = 0;
            _lastRecordingError = null;
            try
            {
                _recorder = new AudioFileRecorder(_recordingPath!, _recordingFormat);
                Title = System.IO.Path.GetFileName(_recordingPath!);
            }
            catch (Exception ex)
            {
                _running = false;
                _lastRecordingError = $"Could not open the recording file: {ex.Message}";
                Faulted?.Invoke(this, _lastRecordingError);
                return;
            }
        }
        else if (_testSink == null)
        {
            // The mix is interleaved IEEE float, so the provider must advertise
            // float too â€” a PCM32 format would reinterpret the float bit patterns
            // as integers and play noise instead of audio.
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

            // Keep only a small cushion so we don't add latency. DiscardOnBufferOverflow
            // makes the render pull fresh audio instead of falling further behind.
            _buffer = new BufferedWaveProvider(format, TimeSpan.FromMilliseconds(120))
            {
                DiscardOnBufferOverflow = true,
            };

            if (_asioDriver != null)
            {
                // ASIO pulls the mix straight out of the render buffer, so the
                // pump below feeds the driver instead of a WASAPI endpoint.
                // A restart needs a fresh handle: the driver is exclusive and
                // the previous one was closed on stop.
                _asioEndpoint = new AsioOutputEndpoint(_asioDriver);
                _asioEndpoint.Start(_buffer);
                _outputLatencyMs = _asioEndpoint.LatencyMs;
                _lowLatency = true;
                _asioSampleRate = _asioEndpoint.SampleRate;

                if (!_asioEndpoint.Streaming)
                {
                    // No clock: the driver's host is not running, so the node
                    // would be silent. Say so instead of failing quietly.
                    Faulted?.Invoke(this,
                        $"ASIO driver '{_asioDriver.Name}' is not streaming (state {_asioEndpoint.State}). " +
                        "Its host application may not be running.");
                }
            }
            else
            {
                // Ask WASAPI for a low-latency shared-mode stream via IAudioClient3.
                // If the device can't grant it, WasapiPlayer transparently falls back
                // to standard shared mode and reports the period it actually got.
                _wasapiOut = new WasapiPlayerBuilder()
                    .WithDevice(_device)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithLowLatency()
                    .WithLatency(5)
                    .Build();

                _wasapiOut.PlaybackStopped += OnPlaybackStopped;
                _wasapiOut.Init(_buffer);
                _wasapiOut.Play();

                // These reflect what the engine actually granted, so read them only
                // after Init/Play — before that they report the requested defaults.
                if (!_wasapiOut.LowLatencyActive &&
                    _wasapiOut.LowLatencyUnavailableReason is { Length: > 0 } reason)
                {
                    Ruse.App.Diagnostics.AppLog.Info(
                        $"Output '{Title}': low latency unavailable ({reason}); using standard shared mode.");
                }

                _lowLatency = _wasapiOut.LowLatencyActive;
                _outputLatencyMs = _wasapiOut.LatencyMilliseconds > 0 ? _wasapiOut.LatencyMilliseconds : 5;
            }
        }

        RefreshRoutes();

        _pumpThread = new Thread(() => PumpLoop(sampleRate, channels))
        {
            IsBackground = true,
            Name = $"OutputPump-{Title}",
            Priority = ThreadPriority.AboveNormal,
        };
        _pumpThread.Start();
    }

    private void PumpLoop(int sampleRate, int channels)
    {
        const int framesPerChunk = 240; // 5ms at 48kHz â€” smaller chunks = lower latency
        var mix = new float[framesPerChunk * channels];
        var temp = new float[framesPerChunk * channels];
        var byteBuffer = new byte[mix.Length * 4];

        if (_recorder != null)
        {
            // A file has no clock of its own, so produce exactly one block per
            // elapsed 5 ms. Writing faster would only run the graph ahead of
            // the live sources.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long produced = 0;
            while (_running)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * sampleRate);
                if (produced < due)
                {
                    RenderChunk(mix, temp, byteBuffer, channels);
                    produced += framesPerChunk;
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

            return;
        }

        if (_testSink != null)
        {
            if (PaceRealTime)
            {
                // Diagnostic path: produce exactly real-time audio so capture
                // timing is exercised the same way the device path does.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long produced = 0;
                while (_running)
                {
                    long due = (long)(clock.Elapsed.TotalSeconds * sampleRate);
                    if (produced < due)
                    {
                        RenderChunk(mix, temp, byteBuffer, channels);
                        produced += framesPerChunk;
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }

                return;
            }

            while (_running)
            {
                RenderChunk(mix, temp, byteBuffer, channels);
                Thread.Sleep(3);
            }

            return;
        }

        // Drive production from what the device has actually consumed: top the
        // render buffer up to a small target instead of pushing on a fixed
        // cadence. This keeps us locked to real time, so capture is never read
        // faster than it is produced (a fixed 5 ms chunk per 3 ms sleep drained
        // the source ring buffer and left the output silent).
        TimeBeginPeriod(1);
        try
        {
            while (_running)
            {
                var buffer = _buffer;
                if (buffer != null && buffer.BufferedDuration.TotalMilliseconds < RenderTargetBufferMs)
                {
                    if (_renderedOnce && buffer.BufferedBytes == 0)
                    {
                        Underruns++;
                    }

                    RenderChunk(mix, temp, byteBuffer, channels);
                    _renderedOnce = true;
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    // Small enough for low latency, large enough to absorb a scheduling hiccup.
    private const double TargetBufferMs = 12;

    // Native plugins do their work on the render thread. A 5 ms pump with a
    // 12 ms cushion is too tight when a VST3 can take a couple of milliseconds
    // and the device pulls every 2 ms, which shows up as periodic dropouts.
    // Keep extra headroom whenever a shared native processor is in the chain.
    private const double NativePluginTargetBufferMs = 24;

    private double RenderTargetBufferMs
    {
        get
        {
            var routes = _routeSnapshot;
            for (int r = 0; r < routes.Length; r++)
            {
                var templates = routes[r].Templates;
                for (int i = 0; i < templates.Length; i++)
                {
                    if (HasNativeProcessor(templates[i]))
                    {
                        return NativePluginTargetBufferMs;
                    }
                }
            }

            return TargetBufferMs;
        }
    }

    private static bool HasNativeProcessor(IAudioProcessor processor)
    {
        if (processor is ISharedProcessor)
        {
            return true;
        }

        if (processor is ChainProcessor chain)
        {
            foreach (var inner in chain.Processors)
            {
                if (HasNativeProcessor(inner))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Set by diagnostics to pace a test sink at real time instead of full speed.</summary>
    public bool PaceRealTime { get; set; }

    /// <summary>Frames that had to be silence-padded because the source underran.</summary>
    public long ShortReadFrames { get; private set; }

    /// <summary>Times the device buffer was empty when the pump wanted to refill it.</summary>
    public long Underruns { get; private set; }

    private bool _renderedOnce;

    /// <summary>Bytes allocated by the render thread while mixing (diagnostics).</summary>
    public long RenderAllocatedBytes { get; private set; }

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    /// <summary>Mixes and emits one pump chunk.</summary>
    internal void RenderChunk(float[] mix, float[] temp, byte[] byteBuffer, int channels)
    {
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            RenderChunkCore(mix, temp, byteBuffer, channels);
        }
        finally
        {
            RenderAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        }
    }

    private void RenderChunkCore(float[] mix, float[] temp, byte[] byteBuffer, int channels)
    {
        Array.Clear(mix, 0, mix.Length);

        var routes = _routeSnapshot;
        for (int r = 0; r < routes.Length; r++)
        {
            var route = routes[r];
            if (route.Source == null || route.Source.Muted)
            {
                continue;
            }

            int read = route.Subscription.Read(temp);
            if (read < temp.Length)
            {
                // Pad a short/empty read with silence instead of stalling: the
                // chain must keep advancing or a delay/latency tail would be
                // stranded whenever a source momentarily underruns.
                ShortReadFrames += (temp.Length - read) / channels;
                Array.Clear(temp, read, temp.Length - read);
                read = temp.Length;
            }

            // Run any modifier effects (and alignment delay) on the freshly
            // pulled block before it joins the mix. The chain is a snapshot so
            // this stays allocation- and lock-free on the render thread.
            route.Process(temp.AsSpan(0, read), read / channels, channels);

            float gain = route.Gain * route.Source.Gain;
            for (int i = 0; i < read; i++)
            {
                mix[i] += temp[i] * gain;
            }
        }

        // Tap processors see the post-effect mix before master gain and
        // clipping, and before any output volume is applied.
        if (_mixSink != null)
        {
            try
            {
                _mixSink(mix, mix.Length);
            }
            catch
            {
                // A tap must never be able to take down the audio path.
            }
        }

        // Hard-clip the mix to avoid wrapping distortion when summing sources.
        bool clipped = false;
        for (int i = 0; i < mix.Length; i++)
        {
            if (!float.IsFinite(mix[i]))
            {
                // A broken effect must never send NaN/Infinity to the device or
                // poison the meter (which feeds the UI).
                mix[i] = 0f;
                clipped = true;
            }
            else if (mix[i] > 1f) { mix[i] = 1f; clipped = true; }
            else if (mix[i] < -1f) { mix[i] = -1f; clipped = true; }
        }

        Clipping = clipped;

        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] *= Volume * MasterGain;
        }

        if (Muted || MasterMuted)
        {
            Array.Clear(mix, 0, mix.Length);
        }

        float sum = 0;
        for (int i = 0; i < mix.Length; i++)
        {
            sum += mix[i] * mix[i];
        }

        float meter = Math.Min(1.0f, MathF.Sqrt(sum / mix.Length) * 3f);
        _meterLevel = float.IsFinite(meter) ? meter : 0f;

        if (_recorder != null)
        {
            _recorder.Write(mix, mix.Length);
        }
        else if (_testSink != null)
        {
            _testSink(mix, mix.Length);
        }
        else
        {
            Buffer.BlockCopy(mix, 0, byteBuffer, 0, byteBuffer.Length);
            _buffer!.AddSamples(byteBuffer, 0, byteBuffer.Length);
            BytesWritten += byteBuffer.Length;
        }
    }

    /// <summary>Total bytes handed to the render endpoint (diagnostics).</summary>
    public long BytesWritten { get; private set; }

    /// <summary>How much audio is queued in the render buffer right now, in ms.</summary>
    public double BufferedMs => _buffer?.BufferedDuration.TotalMilliseconds ?? 0;

    /// <summary>Playback state of the underlying endpoint ("Playing", "Stopped", â€¦).</summary>
    public string PlaybackState => _recorder != null
        ? "Recording"
        : _wasapiOut?.PlaybackState.ToString() ?? (_asioEndpoint?.State ?? "none");

    /// <summary>True when the ASIO driver has delivered at least one buffer.</summary>
    public bool AsioStreaming => _asioEndpoint?.Streaming ?? false;

    /// <summary>Buffer callbacks delivered by the ASIO driver (diagnostics).</summary>
    public long AsioCallbackCount => _asioEndpoint?.CallbackCount ?? 0;

    /// <summary>Rate the endpoint renders at: 48 kHz for WASAPI, the driver's rate for ASIO.</summary>
    public int EndpointSampleRate => _asioSampleRate > 0 ? _asioSampleRate : 48000;

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopRequested)
        {
            return;
        }

        _running = false;
        string reason = e.Exception?.Message ?? "render endpoint stopped";
        Faulted?.Invoke(this, reason);
    }

    public void Stop()
    {
        if (!_running && _wasapiOut == null && _recorder == null)
        {
            return;
        }

        _stopRequested = true;
        _running = false;
        _pumpThread?.Join(500);

        try
        {
            _wasapiOut?.Stop();
            _wasapiOut?.Dispose();
        }
        catch
        {
        }

        _wasapiOut = null;

        try
        {
            _asioEndpoint?.Dispose();
        }
        catch
        {
        }

        _asioEndpoint = null;

        if (_recorder != null)
        {
            try
            {
                _recorder.Stop();
            }
            catch (Exception ex)
            {
                _lastRecordingError = $"Could not finish the recording: {ex.Message}";
            }

            // Read these after Stop: encoders can still fail while flushing
            // their final blocks.
            _lastRecordedSeconds = _recorder.Duration;
            _lastRecordingError = _recorder.LastError ?? _lastRecordingError;

            _recorder.Dispose();
            _recorder = null;
        }

        _buffer = null;
        _meterLevel = 0;
    }

    public void Dispose()
    {
        Stop();
        _device?.Dispose();

        // Release every route's copy of the source audio.
        foreach (var route in _routeSnapshot)
        {
            route.Dispose();
        }

        _routeSnapshot = Array.Empty<Route>();
    }

    public sealed class Route : IDisposable
    {
        private readonly object _chainLock = new();
        private IAudioProcessor[] _instances = Array.Empty<IAudioProcessor>();
        private ParameterBinding[] _bindings = Array.Empty<ParameterBinding>();
        private DelayLine? _compensation;

        public required SourceNode Source { get; init; }
        public float Gain { get; set; } = 1.0f;
        public bool Muted { get; set; }

        /// <summary>
        /// This route's private copy of the source's audio. Taking a copy per
        /// route is what lets one source feed several outputs: without it the
        /// outputs would take turns pulling from the same queue and each would
        /// hear only part of the signal.
        /// </summary>
        private SourceNode.Subscription? _subscription;

        public SourceNode.Subscription Subscription => _subscription ??= Source.Subscribe();

        public void Dispose()
        {
            lock (_chainLock)
            {
                DisposeInstances(_instances);
                _instances = Array.Empty<IAudioProcessor>();
                _bindings = Array.Empty<ParameterBinding>();
                _compensation = null;
                _subscription?.Dispose();
                _subscription = null;
            }
        }

        /// <summary>
        /// The chain's authoritative processors, owned by the modifiers. The UI
        /// edits their parameters; shared native processors are also processed
        /// directly, managed effects are cloned into per-route instances.
        /// </summary>
        public IAudioProcessor[] Templates { get; private set; } = Array.Empty<IAudioProcessor>();

        /// <summary>
        /// Per-route effect instances with private state, so a modifier feeding
        /// several routes no longer smears filter/delay state between them.
        /// Replaced (never mutated in place) when the chain changes.
        /// </summary>
        public IAudioProcessor[] Processors
        {
            get
            {
                lock (_chainLock) return _instances;
            }
            set
            {
                var processors = value ?? Array.Empty<IAudioProcessor>();
                lock (_chainLock)
                {
                    var old = _instances;
                    _instances = processors;
                    _bindings = Array.Empty<ParameterBinding>();
                    _compensation = null;
                    CompensationFrames = 0;
                    InherentLatencyFrames = SumLatency(processors);
                    DisposeInstances(old);
                }
            }
        }

        /// <summary>Latency this route's own effects introduce, in frames.</summary>
        public int InherentLatencyFrames { get; private set; }

        /// <summary>Extra alignment delay applied so parallel routes sum in phase.</summary>
        public int CompensationFrames { get; private set; }

        /// <summary>
        /// Installs a chain: clones managed effects into private instances, reuses
        /// shared native processors, mirrors parameters and inserts the alignment
        /// delay for this route.
        /// </summary>
        public void SetChain(IAudioProcessor[] templates, int compensationFrames)
        {
            var templateArray = templates ?? Array.Empty<IAudioProcessor>();
            int latency = SumLatency(templateArray);
            int compensation = Math.Max(0, compensationFrames);
            var delay = compensation > 0 ? new DelayLine(compensation, 2) : null;

            bool sameChain = Templates.Length == templateArray.Length;
            for (int i = 0; sameChain && i < templateArray.Length; i++)
            {
                sameChain = ReferenceEquals(Templates[i], templateArray[i]);
            }

            if (sameChain)
            {
                lock (_chainLock)
                {
                    InherentLatencyFrames = latency;
                    CompensationFrames = compensation;
                    _compensation = delay;
                }

                return;
            }

            var instances = new IAudioProcessor[templateArray.Length];
            for (int i = 0; i < templateArray.Length; i++)
            {
                // Native plugins own global state and are intentionally shared.
                instances[i] = templateArray[i] is ISharedProcessor
                    ? templateArray[i]
                    : Clone(templateArray[i]);
            }

            var bindings = BuildBindings(templateArray, instances);
            lock (_chainLock)
            {
                var old = _instances;
                Templates = templateArray;
                _instances = instances;
                _bindings = bindings;
                InherentLatencyFrames = latency;
                CompensationFrames = compensation;
                _compensation = delay;
                DisposeInstances(old);
            }
        }

        /// <summary>
        /// Runs the effect chain and any alignment delay on one block. Called on
        /// the output's render thread.
        /// </summary>
        public void Process(Span<float> block, int frames, int channels)
        {
            lock (_chainLock)
            {
                SyncParameters();

                var instances = _instances;
                for (int i = 0; i < instances.Length; i++)
                {
                    // A null entry should never survive SetChain, but never take the
                    // audio thread down if a future chain builder publishes early.
                    instances[i]?.Process(block, frames);
                }

                _compensation?.Process(block, channels);
            }
        }

        // Parameters are edited on the template, so mirror values that changed
        // across the block boundary â€” allocation-free and only on real edits.
        private void SyncParameters()
        {
            for (int i = 0; i < _bindings.Length; i++)
            {
                var binding = _bindings[i];
                float value = binding.Source.Get();
                if (value != binding.Last)
                {
                    binding.Target.Set(value);
                    binding.Last = value;
                }
            }
        }

        private static int SumLatency(IAudioProcessor[] processors)
        {
            int total = 0;
            for (int i = 0; i < processors.Length; i++)
            {
                total += processors[i].LatencySamples;
            }

            return total;
        }

        private static IAudioProcessor Clone(IAudioProcessor template)
        {
            try
            {
                return PresetSerializer.CreateProcessor(PresetSerializer.SerializeProcessor(template)) ?? template;
            }
            catch
            {
                // Unregistered test-only processors fall back to being shared.
                return template;
            }
        }

        private static void DisposeInstances(IAudioProcessor[] instances)
        {
            if (instances.Length == 0)
            {
                return;
            }

            var seen = new HashSet<IAudioProcessor>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < instances.Length; i++)
            {
                var instance = instances[i];
                if (instance is null || instance is ISharedProcessor || !seen.Add(instance))
                {
                    continue;
                }

                (instance as IDisposable)?.Dispose();
            }
        }

        private static ParameterBinding[] BuildBindings(IAudioProcessor[] templates, IAudioProcessor[] instances)
        {
            var bindings = new List<ParameterBinding>();
            for (int i = 0; i < templates.Length && i < instances.Length; i++)
            {
                if (ReferenceEquals(templates[i], instances[i]))
                {
                    continue;
                }

                if (templates[i] is not IParameterizedProcessor source ||
                    instances[i] is not IParameterizedProcessor target)
                {
                    continue;
                }

                foreach (var parameter in source.Parameters)
                {
                    var match = target.Parameters.FirstOrDefault(p => p.Name == parameter.Name);
                    if (match == null)
                    {
                        continue;
                    }

                    float value = parameter.Get();
                    match.Set(value);
                    bindings.Add(new ParameterBinding { Source = parameter, Target = match, Last = value });
                }
            }

            return bindings.ToArray();
        }

        private sealed class ParameterBinding
        {
            public required AudioParameter Source { get; init; }
            public required AudioParameter Target { get; init; }
            public float Last { get; set; }
        }
    }
}
