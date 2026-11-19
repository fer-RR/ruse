using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>A render endpoint with its own mixing thread.</summary>
public sealed class OutputNode : IDisposable
{
    private readonly MMDevice _device;
    private WasapiPlayer? _wasapiOut;
    private OutputWaveBuffer? _buffer;
    private DirectGraphProvider? _directProvider;
    private float[] _directMix = Array.Empty<float>();
    private byte[] _directBytes = Array.Empty<byte>();
    private Thread? _pumpThread;
    private volatile bool _running;
    private AudioWatchdogToken? _watchdog;
    private float _meterLevel;
    private float _meterLevelLeft;
    private float _meterLevelRight;
    private float _meterDbLeft = -60f;
    private float _meterDbRight = -60f;

    // File path: when set, the mix is written to disk instead of a device.
    private AudioFileRecorder? _recorder;
    private string? _recordingPath;
    private AudioFileFormat _recordingFormat = AudioFileFormat.Wav24;
    private double _lastRecordedSeconds;
    private string? _lastRecordingError;
    private int _recorderSkipFrames;

    /// <summary>Persistent recorder control state.</summary>
    public RecorderMode RecorderMode { get; set; } = RecorderMode.Off;
    public bool AutoSave { get; set; }
    public double RecorderSilenceStopSeconds { get; set; }
    public double RecorderStartThresholdDb { get; set; } = -40;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; }
    public string Title { get; set; }
    public float Volume { get; set; } = 1.0f;
    public string? GroupKey { get; set; }
    public bool Solo { get; set; }
    public bool Muted { get; set; }

    /// <summary>Master clip guard for every output; set from settings.</summary>
    public static volatile bool ClipGuardEnabled = true;

    /// <summary>Try IAudioClient3 low-latency shared mode before normal shared mode.</summary>
    public static volatile bool LowLatencyWasapi = true;

    /// <summary>Render graph blocks directly in the WASAPI callback (low-latency mode).</summary>
    public static volatile bool DirectGraphOutput;

    /// <summary>Try exclusive-mode WASAPI when possible.</summary>
    public static volatile bool ExclusiveMode;

    /// <summary>Look-ahead frames used by the master clip guard.</summary>
    public static volatile int ClipGuardLookaheadFrames = 64;

    /// <summary>True while the guard is reducing gain on this output.</summary>
    public bool ClipGuardActive { get; internal set; }

    internal bool BypassClipGuardForTest { get; set; }

    private readonly ClipGuard _clipGuard = new();
    private readonly ClipGuard _nativeClipGuard = new();
    private const int SampleRate = 48000;

    /// <summary>True when this output records to a file rather than a device.</summary>
    public bool IsFileRecorder { get; private set; }

    /// <summary>
    /// True when the shared graph renderer owns this output's mix. Test sinks
    /// use the per-route renderer so engine-level self-tests stay valid.
    /// </summary>
    internal bool UseGraphEngine { get; set; } = true;

    /// <summary>Routes this output through the shared graph renderer.</summary>
    public void EnableGraphEngine() => UseGraphEngine = true;

    /// <summary>True when this output is rendered by the shared graph engine.</summary>
    public bool UsesGraphEngine => UseGraphEngine;

    private SourceNode? _nativeSource;
    private SourceNode.NativeSubscription? _nativeSubscription;
    private int _nativeSampleRate;
    private int _nativeChannels;
    private int _outputSampleRate = 48000;
    private int _recorderSampleRate = 48000;
    private FormatConverter? _recorderConverter;
    private readonly List<float> _recorderConverted = new(16384);
    private float[] _nativeScratch = Array.Empty<float>();
    private float _nativeAppliedGain = float.NaN;
    private IAudioProcessor[] _nativeProcessors = Array.Empty<IAudioProcessor>();
    private IAudioProcessor[] _nativeTemplates = Array.Empty<IAudioProcessor>();
    private Route.ParameterBinding[] _nativeBindings = Array.Empty<Route.ParameterBinding>();
    private readonly List<IAudioProcessor> _retiredNativeProcessors = new();
    private bool _nativeFaultReported;
    private int _pumpFaultStreak;
    private bool _pumpFaultReported;
    private int _disposeRetryQueued;
    private int _resourcesDisposed;

    /// <summary>File sample rate used by this recorder; 48 kHz by default.</summary>
    public int RecorderSampleRate => _recorderSampleRate;

    public void SetRecorderSampleRate(int sampleRate)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Stop recording before changing the sample rate.");
        }

        _recorderSampleRate = sampleRate is 44100 or 48000 or 96000 ? sampleRate : 48000;
    }

    public bool IsNativePassthrough => _nativeSource != null;
    public SourceNode? NativeSource => _nativeSource;

    /// <summary>Set by the renderer when a shared native plugin is on the path.</summary>
    internal bool HasNativePlugin { get; set; }

    /// <summary>Wakes the graph renderer when this pump needs another block.</summary>
    private volatile Action? _engineWake;
    private volatile Action<float[], int>? _mixObserver;
    /// <summary>Optional observer for the post-processing mix. Arguments are the interleaved samples and channel count.</summary>
    public Action<float[], int>? MixObserver
    {
        get => _mixObserver;
        set => _mixObserver = value;
    }
    internal Action? EngineWake
    {
        get => _engineWake;
        set => _engineWake = value;
    }

    private const int EngineRingSeconds = 2;
    private const int MaxEngineLatencyMs = 80;
    private const int TrimEngineLatencyMs = 30;
    private const int EngineTrimFadeFrames = 240;
    private readonly FloatRingBuffer _engineBuffer = new(48000 * 2 * EngineRingSeconds);
    private int _engineTrimFade;
    private float _engineLastLeft;
    private float _engineLastRight;
    private readonly OutputDriftResampler _outputResampler = new();
    private readonly OutputDriftResampler _nativeDriftResampler = new();
    private bool _nativeDriftActive;
    private bool _outputClockResampler;

    internal int ElasticTargetFrames { get; set; }
    internal int ElasticVersion { get; set; } = -1;
    internal long ElasticUnderrunMark { get; set; }
    internal long ElasticLastAdjust { get; set; }

    /// <summary>Audio queued by the graph renderer for this output, in frames.</summary>
    internal double EngineBufferedFrames => _engineBuffer.AvailableToRead / 2.0;

    internal double EngineBufferedMs => EngineBufferedFrames * 1000.0 / 48000;
    public double EngineQueuePercent =>
        _engineBuffer.AvailableToRead / (48000.0 * 2 * EngineRingSeconds) * 100.0;

    internal void SubmitEngineBlock(ReadOnlySpan<float> block) => _engineBuffer.Write(block);

    public void SetOutputClockResampler(bool enabled)
    {
        if (_outputClockResampler == enabled)
        {
            return;
        }

        _outputClockResampler = enabled;
        _outputResampler.RequestReset();
    }

    internal void ClearEngineAudio()
    {
        _engineBuffer.Clear();
        _engineTrimFade = 0;
        _engineLastLeft = 0f;
        _engineLastRight = 0f;
    }

    internal bool ApplyClipGuard(float[] mix, int frames)
    {
        if (BypassClipGuardForTest)
        {
            ClipGuardActive = false;
            return false;
        }

        int lookahead = Math.Clamp(ClipGuardLookaheadFrames, 1, 64);
        if (_clipGuard.LookaheadFrames != lookahead)
        {
            _clipGuard.SetLookahead(lookahead);
        }

        _clipGuard.Process(mix, frames);
        ClipGuardActive = _clipGuard.Engaged;
        return ClipGuardActive;
    }

    /// <summary>Reads one rendered block from the graph ring for offline rendering.</summary>
    public int ReadOfflineBlock(float[] destination) => _engineBuffer.Read(destination);

    internal float DrainEngineLevelForTest(int frames)
    {
        var buffer = new float[Math.Max(1, frames) * 2];
        int read = _engineBuffer.Read(buffer);
        double sum = 0;
        for (int i = 0; i < read; i++)
        {
            sum += Math.Abs(buffer[i]);
        }

        return read == 0 ? 0f : (float)(sum / read);
    }

    internal void SetCompiledState(double chainLatencyMs, bool hasNativePlugin)
    {
        _chainLatencyMs = chainLatencyMs;
        HasNativePlugin = hasNativePlugin;
    }

    internal void SetMixResult(float left, float right, float leftDb, float rightDb, bool clipping)
    {
        _meterLevelLeft = left;
        _meterLevelRight = right;
        _meterLevel = Math.Max(left, right);
        _meterDbLeft = leftDb;
        _meterDbRight = rightDb;
        Clipping = clipping;
    }

    /// <summary>False for recorders restored from a preset.</summary>
    public bool AutoStart { get; set; } = true;
    public bool SaveToSamples { get; set; }

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
    public float MeterLevelLeft => _meterLevelLeft;
    public float MeterLevelRight => _meterLevelRight;
    public float MeterDbLeft => _meterDbLeft;
    public float MeterDbRight => _meterDbRight;
    public bool IsRunning => _running;

    /// <summary>True when the most recent mix block exceeded full scale and was clipped.</summary>
    public volatile bool Clipping;

    /// <summary>Raised when the render endpoint fails or is removed (device unplugged).</summary>
    public event Action<OutputNode, string>? Faulted;

    internal void NotifyStalled(string reason)
    {
        if (_running)
        {
            Faulted?.Invoke(this, reason);
        }
    }

    private volatile bool _stopRequested;

    /// <summary>Approximate endpoint latency, in ms.</summary>
    public double EstimatedLatencyMs
    {
        get
        {
            double queued = _buffer?.BufferedDuration.TotalMilliseconds ?? 0;
            double guard = 0;
            if (ClipGuardEnabled && !BypassClipGuardForTest)
            {
                guard = _nativeSource != null
                    ? _nativeClipGuard.LatencySamples * 1000.0 / Math.Max(1, _nativeSampleRate)
                    : _clipGuard.LatencySamples * 1000.0 / SampleRate;
            }
            return _outputLatencyMs + queued + _chainLatencyMs + guard +
                   (UseGraphEngine ? EngineBufferedMs : 0);
        }
    }

    /// <summary>Latency added by effect chains and alignment, in ms.</summary>
    public double ChainLatencyMs => _chainLatencyMs;
    public double EndpointLatencyMs => _outputLatencyMs;
    public double EngineQueueMs => EngineBufferedMs;

    /// <summary>Current render cushion target, in ms (diagnostics/tests).</summary>
    public double TargetCushionMs => RenderTargetBufferMs;

    private double _outputLatencyMs;
    private double _chainLatencyMs;

    /// <summary>True when the endpoint is actually running in IAudioClient3 low-latency mode.</summary>
    public bool LowLatencyActive => _lowLatency;

    private bool _lowLatency;

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
        // first render. The graph renderer reads each source once and fans it
        // out internally, so it does not need per-route copies. A native
        // passthrough output uses its own native subscription instead.
        if (!UseGraphEngine && _nativeSource == null)
        {
            for (int i = 0; i < routes.Length; i++)
            {
                _ = routes[i].Subscription;
            }
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
    private NetworkPcmSender? _networkSender;
    private string _networkHost = "127.0.0.1";
    private int _networkPort = NetworkPcm.DefaultPort;
    public bool IsNetworkOutput { get; private set; }
    public string NetworkHost => _networkHost;
    public int NetworkPort => _networkPort;
    public string? NetworkError => _networkSender?.LastError;

    public OutputNode(MMDevice device)
    {
        _device = device;
        DeviceId = device.ID;
        Title = device.FriendlyName;
    }

    /// <summary>
    /// Checks whether a source can bypass the 48 kHz graph and feed this
    /// endpoint at its native rate. Channel adaptation inside the driver is
    /// allowed, but sample-rate conversion is not.
    /// </summary>
    public bool CanNativePassthrough(SourceNode source, out string reason)
    {
        reason = "";
        if (IsNetworkOutput)
        {
            reason = "Native passthrough is only available for device or file outputs.";
            return false;
        }

        if (source.Kind == SourceKind.Timeline || source.IsMetronome)
        {
            reason = "Timeline tracks and the metronome are gated by the timeline; native passthrough is not available.";
            return false;
        }

        if (source.SampleRate <= 0)
        {
            reason = "Start the source so its native format is known.";
            return false;
        }

        if (source.Channels is < 1 or > 2)
        {
            reason = $"Native passthrough supports mono or stereo only ({source.Channels} channels detected).";
            return false;
        }

        if (IsFileRecorder)
        {
            if (_recorderSampleRate != source.SampleRate)
            {
                reason = $"The recorder is set to {_recorderSampleRate} Hz; the source is {source.SampleRate} Hz.";
                return false;
            }

            if (!AudioFileFormats.SupportsSampleRate(_recordingFormat, source.SampleRate))
            {
                reason = $"{AudioFileFormats.Get(_recordingFormat).DisplayName} cannot record at {source.SampleRate} Hz.";
                return false;
            }

            return true;
        }

        if (_device != null)
        {
            try
            {
                using var client = _device.CreateAudioClient();
                int mixRate = client.MixFormat.SampleRate;
                if (mixRate > 0 && mixRate != source.SampleRate)
                {
                    reason = $"The device shared mix is {mixRate} Hz; the source is {source.SampleRate} Hz. Set the device to {source.SampleRate} Hz or use the normal route.";
                    return false;
                }

                if (client.MixFormat.Channels > 2)
                {
                    reason = $"The device shared mix has {client.MixFormat.Channels} channels; native passthrough supports mono/stereo endpoints only.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = "Could not query the device format: " + ex.Message;
                return false;
            }
        }

        return true;
    }

    /// <summary>Selects or clears the raw source used by native passthrough.</summary>
    public void SetNativeSource(SourceNode? source) =>
        SetNativeChain(source, Array.Empty<IAudioProcessor>());

    /// <summary>Selects the source and native-rate processor chain.</summary>
    public void SetNativeChain(SourceNode? source, IAudioProcessor[] processors)
    {
        var templates = processors ?? Array.Empty<IAudioProcessor>();
        var instances = new IAudioProcessor[templates.Length];
        for (int i = 0; i < templates.Length; i++)
        {
            // Native taps and shared plugins stay attached to their owners;
            // ordinary processors get a private instance so native rate/state
            // never leaks into the 48 kHz graph chain.
            instances[i] = templates[i] is ISharedProcessor or ITapProcessor
                ? templates[i]
                : Route.Clone(templates[i]);
        }

        var bindings = Route.BuildBindings(templates, instances);
        IAudioProcessor[] oldInstances = _nativeProcessors;
        _nativeTemplates = templates;
        _nativeProcessors = instances;
        _nativeBindings = bindings;

        bool sameSource = ReferenceEquals(_nativeSource, source);
        if (!sameSource)
        {
            _nativeSubscription?.Dispose();
            _nativeSubscription = null;
            _nativeSource = source;
            _nativeSampleRate = source?.SampleRate ?? 0;
            _nativeChannels = source?.Channels ?? 0;
            _nativeFaultReported = false;
            _nativeAppliedGain = float.NaN;
            if (source != null && source.SampleRate > 0)
            {
                _nativeSubscription = source.SubscribeNative();
            }
        }

        if (_nativeSampleRate > 0)
        {
            for (int i = 0; i < instances.Length; i++)
            {
                (instances[i] as INativeProcessor)?.ConfigureNative(_nativeSampleRate, 2);
            }
        }

        RetireNativeInstances(oldInstances, instances);
    }

    private void RetireNativeInstances(IAudioProcessor[] oldInstances, IAudioProcessor[] current)
    {
        if (oldInstances.Length == 0)
        {
            return;
        }

        for (int i = 0; i < oldInstances.Length; i++)
        {
            var instance = oldInstances[i];
            if (instance == null || instance is ISharedProcessor or ITapProcessor ||
                current.Contains(instance))
            {
                continue;
            }

            if (IsRunning)
            {
                _retiredNativeProcessors.Add(instance);
            }
            else
            {
                (instance as IDisposable)?.Dispose();
            }
        }
    }

    private void DisposeRetiredNativeInstances()
    {
        if (_retiredNativeProcessors.Count == 0)
        {
            return;
        }

        foreach (var instance in _retiredNativeProcessors)
        {
            (instance as IDisposable)?.Dispose();
        }

        _retiredNativeProcessors.Clear();
    }

    /// <summary>Creates a device-less output that pushes mixed frames to a sink.</summary>
    public static OutputNode CreateForTest(Action<float[], int> sink)
    {
        var node = new OutputNode();
        node.UseGraphEngine = false;
        node.BypassClipGuardForTest = true;
        node._testSink = sink;
        return node;
    }

    /// <summary>Creates an output that sends the mix to another Ruse instance.</summary>
    public static OutputNode CreateForNetwork(string host, int port)
    {
        var node = new OutputNode("network:" + Guid.NewGuid().ToString("N"), "Network output")
        {
            IsNetworkOutput = true,
            UseGraphEngine = false,
            PaceRealTime = true,
            _networkHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host,
            _networkPort = Math.Clamp(port, 1, 65535),
        };
        return node;
    }

    /// <summary>Changes the network destination while the sender is stopped.</summary>
    public void SetNetworkDestination(string host, int port)
    {
        bool wasRunning = IsRunning;
        Stop();
        _networkHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        _networkPort = Math.Clamp(port, 1, 65535);
        if (wasRunning)
        {
            Start();
        }
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

        if (_pumpThread != null)
        {
            Stop();
            if (_pumpThread != null)
            {
                return;
            }
        }

        if (_wasapiOut != null)
        {
            ResetFailedStart();
        }

        bool native = _nativeSource != null;
        int pumpRate = native ? _nativeSource!.SampleRate : 48000;
        bool nativeProcessed = native && _nativeProcessors.Length > 0;
        int pumpChannels = native
            ? (nativeProcessed || (_nativeSource!.Channels == 1 && !IsFileRecorder)
                ? 2
                : _nativeSource!.Channels)
            : 2;
        int recordRate = native ? pumpRate : _recorderSampleRate;
        int recordChannels = native
            ? (nativeProcessed ? 2 : _nativeSource!.Channels)
            : 2;
        if (native)
        {
            if (!CanNativePassthrough(_nativeSource!, out string reason))
            {
                _running = false;
                Faulted?.Invoke(this, reason);
                return;
            }

            _nativeSampleRate = pumpRate;
            _nativeChannels = _nativeSource!.Channels;
            if (_nativeSubscription == null || _nativeSubscription.Invalidated)
            {
                _nativeSubscription?.Dispose();
                _nativeSubscription = _nativeSource!.SubscribeNative();
            }

            if (_nativeSubscription == null)
            {
                _running = false;
                Faulted?.Invoke(this, "The source does not have a native mono/stereo stream yet.");
                return;
            }

            for (int i = 0; i < _nativeProcessors.Length; i++)
            {
                (_nativeProcessors[i] as INativeProcessor)?.
                    ConfigureNative(pumpRate, pumpChannels);
            }

            Ruse.App.Diagnostics.AppLog.Info(
                $"Output '{Title}': native passthrough {pumpRate} Hz, " +
                $"source {_nativeSource.Channels}ch, endpoint {pumpChannels}ch, " +
                $"processors={_nativeProcessors.Length}, recorder={IsFileRecorder}.");

            int nativeBlockFrames = Math.Max(1, (int)Math.Round(pumpRate * 0.005));
            int nativeScratch = nativeBlockFrames * Math.Max(1, _nativeChannels);
            if (_nativeScratch.Length < nativeScratch)
            {
                _nativeScratch = new float[nativeScratch];
            }

            _nativeFaultReported = false;
            _nativeClipGuard.ConfigureSampleRate(pumpRate);
            _nativeClipGuard.SetLookahead(Math.Clamp(
                (int)Math.Round(ClipGuardLookaheadFrames * pumpRate / 48000.0), 1, 256));
            _nativeClipGuard.Reset();
            _nativeDriftResampler.Reset();
            _nativeDriftActive = false;
        }

        _outputSampleRate = IsFileRecorder ? recordRate : pumpRate;
        _stopRequested = false;
        Underruns = 0;
        NativeSkippedFrames = 0;
        _outputPpm = 0;
        _outputPpmReady = false;
        _renderedOnce = false;
        _outputClock.Restart();
        _outputClockFrames = 0;
        _outputClockLastTicks = 0;
        _startedAt = Environment.TickCount64;
        _lastUnderrunTick = 0;
        _lastPenaltyDecayTick = 0;
        _underrunBurst = 0;
        _latencyDowngrades = 0;
        _latencyPenaltyMs = 0;
        ClearEngineAudio();
        _outputResampler.Reset();
        _clipGuard.Reset();
        _nativeAppliedGain = float.NaN;
        ClipGuardActive = false;
        _pumpFaultStreak = 0;
        _pumpFaultReported = false;

        try
        {
        if (IsNetworkOutput)
        {
            _networkSender?.Dispose();
            _networkSender = new NetworkPcmSender(_networkHost, _networkPort);
            _testSink = _networkSender.Send;
            _outputLatencyMs = 0;
        }

        if (IsFileRecorder)
        {
            _lastRecordedSeconds = 0;
            _lastRecordingError = null;
            try
            {
                _recorder = new AudioFileRecorder(
                    _recordingPath!, _recordingFormat, recordRate, recordChannels);
                _recorderSkipFrames = _nativeSource == null && ClipGuardEnabled && !BypassClipGuardForTest
                    ? _clipGuard.LatencySamples
                    : 0;
                _recorderConverter = native || recordRate == 48000
                    ? null
                    : new FormatConverter(2, 48000, recordRate);
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
            // float too — a PCM32 format would reinterpret the float bit patterns
            // as integers and play noise instead of audio.
            var format = WaveFormat.CreateIeeeFloatWaveFormat(pumpRate, pumpChannels);
            bool direct = DirectGraphOutput && UseGraphEngine && _nativeSource == null;
            IWaveProvider provider;

            if (direct)
            {
                _directMix = new float[8192 * 2];
                _directBytes = new byte[_directMix.Length * sizeof(float)];
                _directProvider = new DirectGraphProvider(this, format);
                provider = _directProvider;
            }
            else
            {
                // Keep only a small cushion so we don't add latency. DiscardOnBufferOverflow
                // makes the render pull fresh audio instead of falling further behind.
                _buffer = new OutputWaveBuffer(format, 120);
                provider = _buffer;
            }

            {
                _wasapiOut = OpenWasapiOutput(provider, ExclusiveMode);
                _wasapiOut.PlaybackStopped += OnPlaybackStopped;

                // These reflect what the engine actually granted, so read them only
                // after Init — before that they report the requested defaults.
                if (!_wasapiOut.LowLatencyActive &&
                    _wasapiOut.LowLatencyUnavailableReason is { Length: > 0 } reason)
                {
                    Ruse.App.Diagnostics.AppLog.Info(
                        $"Output '{Title}': low latency unavailable ({reason}); using standard shared mode.");
                }

                _lowLatency = _wasapiOut.LowLatencyActive;
                _outputLatencyMs = _wasapiOut.LatencyMilliseconds > 0 ? _wasapiOut.LatencyMilliseconds : 5;
                _outputSampleRate = _wasapiOut.OutputWaveFormat.SampleRate > 0
                    ? _wasapiOut.OutputWaveFormat.SampleRate
                    : pumpRate;

                if (native && _wasapiOut.OutputWaveFormat.SampleRate != pumpRate)
                {
                    Faulted?.Invoke(this,
                        $"The device opened at {_wasapiOut.OutputWaveFormat.SampleRate} Hz instead of {pumpRate} Hz; native passthrough would be resampled.");
                    ResetFailedStart();
                    return;
                }

                if (direct)
                {
                    _running = true;
                    RefreshRoutes();
                    Ruse.App.Diagnostics.AppLog.Info($"Output '{Title}': direct graph callback at {_outputLatencyMs:0.0} ms endpoint latency.");
                    _wasapiOut.Play();
                    return;
                }

                _wasapiOut.Play();
            }
        }

        RefreshRoutes();

        _watchdog = AudioThreadWatchdog.Register($"OutputPump-{Title}", TimeSpan.FromSeconds(5),
            () => NotifyStalled("The output pump stopped responding."));
        _running = true;
        _pumpThread = new Thread(() => PumpLoop(pumpRate, pumpChannels))
        {
            IsBackground = true,
            Name = $"OutputPump-{Title}",
            Priority = ThreadPriority.AboveNormal,
        };
        _pumpThread.Start();
        }
        catch
        {
            ResetFailedStart();
            throw;
        }
    }

    private void ResetFailedStart()
    {
        _stopRequested = true;
        _running = false;
        var watchdog = _watchdog;
        _watchdog = null;
        watchdog?.Dispose();
        if (IsNetworkOutput)
        {
            _networkSender?.Dispose();
            _networkSender = null;
            _testSink = null;
        }

        var wasapi = _wasapiOut;
        _wasapiOut = null;
        if (wasapi != null)
        {
            wasapi.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                wasapi.Dispose();
            }
            catch
            {
            }
        }
        _buffer = null;
        _directProvider = null;
        _directMix = Array.Empty<float>();
        _directBytes = Array.Empty<byte>();
        var recorder = _recorder;
        _recorder = null;
        _recorderSkipFrames = 0;
        _recorderConverter = null;
        _recorderConverted.Clear();
        recorder?.Dispose();
    }

    private void PumpLoop(int sampleRate, int channels)
    {
        using var mmcss = MmcssScope.Enter(priority: AvrtPriority.Critical);
        bool native = _nativeSource != null;
        int framesPerChunk = native
            ? Math.Max(1, (int)Math.Round(sampleRate * 0.005))
            : AudioEngineConfig.BlockFrames;
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
                _watchdog?.Beat();
                long due = (long)(clock.Elapsed.TotalSeconds * sampleRate);
                if (produced < due)
                {
                    RenderChunkSafely(mix, temp, byteBuffer, channels);
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
                    _watchdog?.Beat();
                    long due = (long)(clock.Elapsed.TotalSeconds * sampleRate);
                    if (produced < due)
                    {
                        RenderChunkSafely(mix, temp, byteBuffer, channels);
                        produced += framesPerChunk;
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }

                return;
            }

            if (UnpacedTestSink)
            {
                while (_running)
                {
                    _watchdog?.Beat();
                    RenderChunkSafely(mix, temp, byteBuffer, channels);
                }

                return;
            }

            while (_running)
            {
                _watchdog?.Beat();
                RenderChunkSafely(mix, temp, byteBuffer, channels);
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
        using var timer = HighResolutionTimer.TryCreate();
        try
        {
            while (_running)
            {
                _watchdog?.Beat();
                var buffer = _buffer;
                if (buffer != null && buffer.BufferedDuration.TotalMilliseconds < RenderTargetBufferMs)
                {
                    if (_renderedOnce && buffer.BufferedBytes == 0)
                    {
                        Underruns++;
                    }

                    RenderChunkSafely(mix, temp, byteBuffer, channels);
                    _renderedOnce = true;
                }
                else
                {
                    double waitMs = buffer == null
                        ? 1.0
                        : buffer.BufferedDuration.TotalMilliseconds - RenderTargetBufferMs - 0.5;
                    long waitTicks = (long)(Math.Max(0.0, waitMs) * System.Diagnostics.Stopwatch.Frequency / 1000.0);
                    if (waitTicks > 0 && timer != null && timer.Wait(waitTicks))
                    {
                        continue;
                    }

                    Thread.Sleep(waitMs > 0 ? 1 : 0);
                }
            }
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    private WasapiPlayer OpenWasapiOutput(IWaveProvider provider, bool exclusive)
    {
        try
        {
            return Open(exclusive);
        }
        catch (Exception ex) when (exclusive)
        {
            Ruse.App.Diagnostics.AppLog.Warning(
                $"Output '{Title}': exclusive mode unavailable; using shared mode. {ex.Message}");
            return Open(false);
        }

        WasapiPlayer Open(bool wantExclusive)
        {
            var builder = new WasapiPlayerBuilder()
                .WithDevice(_device)
                .WithEventSync()
                .WithMmcssThreadPriority("Pro Audio");

            if (wantExclusive)
            {
                builder = builder
                    .WithExclusiveMode()
                    .WithLatency(Math.Clamp(ChooseWasapiLatencyMs(), 3, 10));
            }
            else
            {
                builder = builder.WithSharedMode();
                if (LowLatencyWasapi)
                {
                    builder = builder.WithLowLatency();
                }

                builder = builder.WithLatency(ChooseWasapiLatencyMs());
            }

            WasapiPlayer player = builder.Build();
            try
            {
                player.Init(provider);
                if (wantExclusive)
                {
                    Ruse.App.Diagnostics.AppLog.Info(
                        $"Output '{Title}': exclusive WASAPI stream opened at {player.LatencyMilliseconds} ms.");
                }

                return player;
            }
            catch
            {
                player.Dispose();
                throw;
            }
        }
    }

    private int ChooseWasapiLatencyMs()
    {
        try
        {
            using var client = _device.CreateAudioClient();
            var mix = client.MixFormat;
            var info = client.GetSharedModeEnginePeriod(mix);
            if (info.MinPeriodInFrames == 0)
            {
                return 5;
            }

            double milliseconds = info.MinPeriodInFrames * 1000.0 / Math.Max(1, mix.SampleRate);
            return Math.Clamp((int)Math.Ceiling(milliseconds), 3, 10);
        }
        catch
        {
            // IAudioClient3 is optional; use the conservative shared-mode value.
            return 5;
        }
    }

    // Small enough for low latency, large enough to absorb a scheduling hiccup.
    private const double TargetBufferMs = 12;

    /// <summary>Target one endpoint period instead of two when true.</summary>
    public static volatile bool AggressiveCushion;

    // Native plugins start at 6 ms. A route that keeps underrunning steps up
    // to 10/14/18 ms through the automatic cushion downgrade.
    private const double NativePluginTargetBufferMs = 6;

    private double RenderTargetBufferMs
    {
        get
        {
            DecayLatencyPenalty();
            int manualMs = AudioEngineConfig.OutputBufferManualMs;
            if (manualMs > 0)
            {
                return Math.Clamp(manualMs + _latencyPenaltyMs, 2, 40);
            }

            if (HasNativePlugin || (!UseGraphEngine && RoutesHaveNativeProcessor()))
            {
                return Math.Min(30, NativePluginTargetBufferMs + _latencyPenaltyMs);
            }

            // Keep roughly two device periods buffered; that is the minimum
            // that survives one late pump wake without inserting silence.
            // Aggressive mode keeps one period instead.
            double endpointMs = _outputLatencyMs;

            if (endpointMs <= 0)
            {
                endpointMs = AggressiveCushion ? 4 : TargetBufferMs;
            }

            int periods = AudioEngineConfig.OutputBufferPeriods;
            if (periods <= 0)
            {
                periods = AggressiveCushion ? 1 : 2;
            }

            double minimum = periods > 1 ? 6 : 3;
            return Math.Clamp(endpointMs * periods + _latencyPenaltyMs, minimum, 24);
        }
    }

    private bool RoutesHaveNativeProcessor()
    {
        var routes = _routeSnapshot;
        for (int r = 0; r < routes.Length; r++)
        {
            var templates = routes[r].Templates;
            for (int i = 0; i < templates.Length; i++)
            {
                if (HasNativeProcessor(templates[i]))
                {
                    return true;
                }
            }
        }

        return false;
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

    /// <summary>Runs a test sink as fast as the mixer can produce blocks.</summary>
    internal bool UnpacedTestSink { get; set; }

    /// <summary>Frames that had to be silence-padded because the source underran.</summary>
    public long ShortReadFrames { get; private set; }

    /// <summary>Times the device buffer was empty when the pump wanted to refill it.</summary>
    public long Underruns { get; private set; }

    private bool _renderedOnce;
    private readonly System.Diagnostics.Stopwatch _outputClock = new();
    private double _outputPpm;
    private bool _outputPpmReady;
    private long _outputClockFrames;
    private long _outputClockLastTicks;
    private long _startedAt;
    private long _lastUnderrunTick;
    private long _lastPenaltyDecayTick;
    private int _underrunBurst;
    private int _latencyDowngrades;
    private double _latencyPenaltyMs;

    /// <summary>Bytes allocated by the render thread while mixing (diagnostics).</summary>
    public long RenderAllocatedBytes { get; private set; }

    public static bool MeasureAllocations { get; set; }

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uPeriod);

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uPeriod);

    /// <summary>Mixes and emits one pump chunk.</summary>
    internal void RenderOrDrainChunk(float[] mix, float[] temp, byte[] byteBuffer, int channels)
    {
        if (_nativeSource != null)
        {
            RenderNativeChunk(mix, byteBuffer, channels);
            return;
        }

        if (UseGraphEngine)
        {
            DrainEngineChunk(mix, byteBuffer, channels);
            return;
        }

        RenderChunk(mix, temp, byteBuffer, channels);
    }

    /// <summary>
    /// Renders one block for the pump. A failing effect or device must silence
    /// this block, not kill the pump thread (and with it the whole process).
    /// </summary>
    private void RenderChunkSafely(float[] mix, float[] temp, byte[] byteBuffer, int channels)
    {
        try
        {
            RenderOrDrainChunk(mix, temp, byteBuffer, channels);
            _pumpFaultStreak = 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AudioFaultLog.ReportOutputPumpFault(Title, ex);
            Array.Clear(mix, 0, mix.Length);
            WriteSilence(mix, byteBuffer, channels);

            if (++_pumpFaultStreak >= 50 && !_pumpFaultReported)
            {
                _pumpFaultReported = true;
                _stopRequested = true;
                _running = false;
                Faulted?.Invoke(this, "The output pump hit repeated errors and was stopped.");
            }
        }
    }

    private void WriteSilence(float[] mix, byte[] byteBuffer, int channels)
    {
        if (_recorder != null || _testSink != null || _buffer == null)
        {
            return;
        }

        try
        {
            Buffer.BlockCopy(mix, 0, byteBuffer, 0, byteBuffer.Length);
            _buffer.Write(byteBuffer, 0, byteBuffer.Length);
            BytesWritten += byteBuffer.Length;
            ReportOutputClock(byteBuffer.Length, channels);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The device path is already failing; keep the pump alive and retry.
        }
    }

    /// <summary>Copies native-rate source audio straight to the endpoint.</summary>
    private void RenderNativeChunk(float[] mix, byte[] byteBuffer, int channels)
    {
        var source = _nativeSource;
        var subscription = _nativeSubscription;
        if (source == null || subscription == null ||
            subscription.Invalidated ||
            source.SampleRate != _nativeSampleRate ||
            source.Channels != _nativeChannels)
        {
            Array.Clear(mix);
            if (!_nativeFaultReported)
            {
                _nativeFaultReported = true;
                Faulted?.Invoke(this, "The source format changed; native passthrough stopped.");
            }

            return;
        }

        int sourceChannels = _nativeChannels;
        int outFrames = mix.Length / Math.Max(1, channels);
        if (subscription.BufferedMs > 200 && subscription.SampleRate > 0)
        {
            // A native path has no resampler, so let a lagging endpoint trim
            // the backlog instead of growing latency forever.
            int bufferedFrames = (int)(subscription.BufferedMs * subscription.SampleRate / 1000.0);
            int keepFrames = (int)(80.0 * subscription.SampleRate / 1000.0);
            int excess = bufferedFrames - keepFrames;
            if (excess > 0)
            {
                NativeSkippedFrames += subscription.Skip(excess);
            }
        }

        bool driftCorrection = sourceChannels == channels &&
            (_nativeDriftActive || (double.IsFinite(source.DriftPpm) && Math.Abs(source.DriftPpm) > 5));
        if (driftCorrection)
        {
            if (!_nativeDriftActive)
            {
                _nativeDriftActive = true;
                _nativeDriftResampler.Reset();
                ThreadPool.QueueUserWorkItem(_ =>
                    Ruse.App.Diagnostics.AppLog.Info(
                        $"Output '{Title}': native clock drift {source.DriftPpm:0.0} ppm; enabling drift resampler."));
            }

            double ratio = 1.0 + (Math.Clamp(source.DriftPpm, -500.0, 500.0) * 1e-6);
            _nativeDriftResampler.Process(
                subscription.Buffer, mix.AsSpan(0, outFrames * channels), channels, ratio);
        }
        else if (sourceChannels == channels)
        {
            int read = subscription.Read(mix);
            if (read < mix.Length)
            {
                ShortReadFrames += (mix.Length - read) / Math.Max(1, channels);
                Array.Clear(mix, read, mix.Length - read);
            }
        }
        else
        {
            if (_nativeScratch.Length < outFrames * sourceChannels)
            {
                Array.Clear(mix);
                if (!_nativeFaultReported)
                {
                    _nativeFaultReported = true;
                    Faulted?.Invoke(this, "Native passthrough buffer was too small; restart the route.");
                }

                return;
            }

            int read = subscription.Read(_nativeScratch);
            int readFrames = read / Math.Max(1, sourceChannels);
            for (int frame = 0; frame < outFrames; frame++)
            {
                int sourceIndex = frame < readFrames ? frame * sourceChannels : -1;
                float left = sourceIndex >= 0 ? _nativeScratch[sourceIndex] : 0f;
                float right = sourceIndex >= 0 && sourceChannels > 1
                    ? _nativeScratch[sourceIndex + 1]
                    : left;
                mix[frame * channels] = left;
                if (channels > 1)
                {
                    mix[(frame * channels) + 1] = right;
                }
            }

            if (readFrames < outFrames)
            {
                ShortReadFrames += outFrames - readFrames;
            }
        }

        var processors = _nativeProcessors;
        Route.SyncParameters(_nativeBindings);
        for (int i = 0; i < processors.Length; i++)
        {
            try
            {
                processors[i].Process(mix.AsSpan(0, mix.Length), outFrames);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AudioFaultLog.ReportEffectFault(processors[i].DisplayName, ex);
                Array.Clear(mix);
                break;
            }
        }

        bool sourceSoloSuppressed = MixerGroupBus.AnySourceSolo &&
                                    !source.Solo && !MixerGroupBus.Solo(source.GroupKey);
        bool outputSoloSuppressed = MixerGroupBus.AnyOutputSolo &&
                                    !Solo && !MixerGroupBus.Solo(GroupKey);
        float targetGain = Muted || MasterMuted || source.Muted ||
            MixerGroupBus.Muted(GroupKey) || MixerGroupBus.Muted(source.GroupKey) ||
            sourceSoloSuppressed || outputSoloSuppressed
            ? 0f
            : Volume * MasterGain * source.Gain *
              MixerGroupBus.Gain(GroupKey) * MixerGroupBus.Gain(source.GroupKey);

        if (float.IsNaN(_nativeAppliedGain))
        {
            _nativeAppliedGain = targetGain;
        }

        if (targetGain != _nativeAppliedGain)
        {
            float step = (targetGain - _nativeAppliedGain) / Math.Max(1, outFrames);
            for (int frame = 0; frame < outFrames; frame++)
            {
                _nativeAppliedGain += step;
                int index = frame * channels;
                for (int channel = 0; channel < channels; channel++)
                {
                    mix[index + channel] *= _nativeAppliedGain;
                }
            }

            _nativeAppliedGain = targetGain;
        }
        else if (targetGain != 1f)
        {
            for (int i = 0; i < mix.Length; i++)
            {
                mix[i] *= targetGain;
            }
        }

        if (ClipGuardEnabled && !BypassClipGuardForTest)
        {
            _nativeClipGuard.Process(mix.AsSpan(0, outFrames * channels), outFrames);
            ClipGuardActive = _nativeClipGuard.Engaged;
        }
        else
        {
            ClipGuardActive = false;
        }

        double sumLeft = 0;
        double sumRight = 0;
        float peak = 0;
        int frames = Math.Max(1, mix.Length / channels);
        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            float left = mix[index];
            float right = channels > 1 ? mix[index + 1] : left;
            sumLeft += left * left;
            sumRight += right * right;
            peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
        }

        float meterLeft = Math.Min(1.0f, MathF.Sqrt((float)(sumLeft / frames)) * 3f);
        float meterRight = Math.Min(1.0f, MathF.Sqrt((float)(sumRight / frames)) * 3f);
        _meterLevelLeft = float.IsFinite(meterLeft) ? meterLeft : 0f;
        _meterLevelRight = float.IsFinite(meterRight) ? meterRight : 0f;
        _meterLevel = Math.Max(_meterLevelLeft, _meterLevelRight);
        _meterDbLeft = (float)(20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumLeft / frames))));
        _meterDbRight = (float)(20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumRight / frames))));
        Clipping = peak > 1f;

        WriteSink(mix, byteBuffer, channels);
    }

    /// <summary>Emits one block produced by the shared graph renderer.</summary>
    private void DrainEngineChunk(float[] mix, byte[] byteBuffer, int channels)
    {
        int availableFrames = _engineBuffer.AvailableToRead / 2;
        int maxFrames = 48000 * MaxEngineLatencyMs / 1000;
        if (!IsFileRecorder && availableFrames > maxFrames)
        {
            int keepFrames = 48000 * TrimEngineLatencyMs / 1000;
            int dropped = _engineBuffer.Skip((availableFrames - keepFrames) * 2) / 2;
            if (dropped > 0)
            {
                _engineTrimFade = EngineTrimFadeFrames;
            }
        }

        if (_outputClockResampler && _outputPpmReady && Math.Abs(_outputPpm) > 5)
        {
            _outputResampler.Process(
                _engineBuffer, mix, channels, 1.0 + (_outputPpm * 1e-6));
            _renderedOnce = true;
            WriteSink(mix, byteBuffer, channels);

            var resamplerWake = _engineWake;
            if (resamplerWake != null)
            {
                try
                {
                    resamplerWake();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            return;
        }

        int read = _engineBuffer.Read(mix);
        if (read < mix.Length)
        {
            Array.Clear(mix, read, mix.Length - read);
        }

        if (read < mix.Length)
        {
            if (_renderedOnce)
            {
                Underruns++;
                RegisterUnderrun();
            }

            ShortReadFrames += (mix.Length - read) / channels;
        }

        if (_engineTrimFade > 0 && read >= 2)
        {
            int frames = read / 2;
            int fade = Math.Min(frames, _engineTrimFade);
            for (int frame = 0; frame < fade; frame++)
            {
                float t = 1f - ((_engineTrimFade - frame) / (float)EngineTrimFadeFrames);
                int index = frame * 2;
                mix[index] = _engineLastLeft + ((mix[index] - _engineLastLeft) * t);
                mix[index + 1] = _engineLastRight + ((mix[index + 1] - _engineLastRight) * t);
            }

            _engineTrimFade -= fade;
        }

        if (read >= 2)
        {
            _engineLastLeft = mix[read - 2];
            _engineLastRight = mix[read - 1];
        }

        _renderedOnce = true;
        WriteSink(mix, byteBuffer, channels);

        var wake = _engineWake;
        if (wake != null)
        {
            try
            {
                wake();
            }
            catch (ObjectDisposedException)
            {
                // The engine was disposed while this pump was finishing.
            }
        }
    }

    private void ReadDirect(Span<byte> destination)
    {
        int frameBytes = sizeof(float) * 2;
        int frames = destination.Length / frameBytes;
        if (frames <= 0 || _directMix.Length < frames * 2)
        {
            destination.Clear();
            return;
        }

        try
        {
            FillDirectMix(frames);
            int bytes = frames * frameBytes;
            Buffer.BlockCopy(_directMix, 0, _directBytes, 0, bytes);
            _directBytes.AsSpan(0, bytes).CopyTo(destination[..bytes]);
            if (destination.Length > bytes)
            {
                destination[bytes..].Clear();
            }

            BytesWritten += bytes;
            ReportOutputClock(bytes, 2);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            destination.Clear();
            ThreadPool.QueueUserWorkItem(_ =>
                Ruse.App.Diagnostics.AppLog.Warning(
                    $"Output '{Title}' direct callback failed: {ex.Message}"));
        }
    }

    private void FillDirectMix(int frames)
    {
        const int channels = 2;
        int sampleCount = frames * channels;
        int availableFrames = _engineBuffer.AvailableToRead / channels;
        int maxFrames = 48000 * MaxEngineLatencyMs / 1000;
        if (!IsFileRecorder && availableFrames > maxFrames)
        {
            int keepFrames = 48000 * TrimEngineLatencyMs / 1000;
            int dropped = _engineBuffer.Skip((availableFrames - keepFrames) * channels) / channels;
            if (dropped > 0)
            {
                _engineTrimFade = EngineTrimFadeFrames;
            }
        }

        if (_outputClockResampler && _outputPpmReady && Math.Abs(_outputPpm) > 5)
        {
            _outputResampler.Process(
                _engineBuffer, _directMix.AsSpan(0, sampleCount), channels,
                1.0 + (_outputPpm * 1e-6));
            _renderedOnce = true;
            WakeEngine();
            return;
        }

        int read = _engineBuffer.Read(_directMix.AsSpan(0, sampleCount));
        if (read < sampleCount)
        {
            _directMix.AsSpan(read, sampleCount - read).Clear();
            if (_renderedOnce)
            {
                Underruns++;
                RegisterUnderrun();
            }

            ShortReadFrames += (sampleCount - read) / channels;
        }

        if (_engineTrimFade > 0 && read >= channels)
        {
            int readFrames = read / channels;
            int fade = Math.Min(readFrames, _engineTrimFade);
            for (int frame = 0; frame < fade; frame++)
            {
                float t = 1f - ((_engineTrimFade - frame) / (float)EngineTrimFadeFrames);
                int index = frame * channels;
                _directMix[index] = _engineLastLeft + ((_directMix[index] - _engineLastLeft) * t);
                _directMix[index + 1] = _engineLastRight + ((_directMix[index + 1] - _engineLastRight) * t);
            }

            _engineTrimFade -= fade;
        }

        if (read >= channels)
        {
            _engineLastLeft = _directMix[read - 2];
            _engineLastRight = _directMix[read - 1];
        }

        _renderedOnce = true;
        WakeEngine();
    }

    private void WakeEngine()
    {
        var wake = _engineWake;
        if (wake == null)
        {
            return;
        }

        try
        {
            wake();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RegisterUnderrun()
    {
        if (!UseGraphEngine || _latencyDowngrades >= 3)
        {
            return;
        }

        long now = Environment.TickCount64;

        // Ignore the prefill at start: the first few blocks are silence by design.
        if (now - _startedAt < 2000)
        {
            return;
        }

        if (now - _lastUnderrunTick < 1000)
        {
            _underrunBurst++;
        }
        else
        {
            _underrunBurst = 1;
        }

        _lastUnderrunTick = now;
        if (_underrunBurst < 3)
        {
            return;
        }

        _underrunBurst = 0;
        _latencyDowngrades++;
        _latencyPenaltyMs += 4;
        string message =
            $"Output '{Title}' is underrunning; adding 4 ms of latency cushion " +
            $"(total +{_latencyPenaltyMs:0} ms, step {_latencyDowngrades}/3).";
        ThreadPool.QueueUserWorkItem(_ => Ruse.App.Diagnostics.AppLog.Info(message));
    }

    private void DecayLatencyPenalty()
    {
        if (_latencyPenaltyMs <= 0)
        {
            return;
        }

        long now = Environment.TickCount64;
        long stableSince = Math.Max(_startedAt, _lastUnderrunTick);
        if (now - stableSince < 30_000 || now - _lastPenaltyDecayTick < 30_000)
        {
            return;
        }

        _lastPenaltyDecayTick = now;
        _latencyPenaltyMs = Math.Max(0, _latencyPenaltyMs - 2);
        _latencyDowngrades = Math.Max(0, _latencyDowngrades - 1);
    }

    private void WriteSink(float[] mix, byte[] byteBuffer, int channels)
    {
        var observer = MixObserver;
        if (observer != null)
        {
            try
            {
                observer(mix, channels);
            }
            catch
            {
            }
        }

        if (_recorder != null)
        {
            int skipFrames = Math.Min(_recorderSkipFrames, mix.Length / 2);
            _recorderSkipFrames -= skipFrames;
            int offset = skipFrames * 2;
            if (offset < mix.Length)
            {
                if (_recorderConverter == null)
                {
                    _recorder.Write(mix.AsSpan(offset, mix.Length - offset));
                }
                else
                {
                    _recorderConverted.Clear();
                    _recorderConverter.Convert(mix.AsSpan(offset, mix.Length - offset), _recorderConverted);
                    _recorder.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_recorderConverted));
                }
            }
        }
        else if (_testSink != null)
        {
            _testSink(mix, mix.Length);
        }
        else
        {
            Buffer.BlockCopy(mix, 0, byteBuffer, 0, byteBuffer.Length);
            _buffer!.Write(byteBuffer, 0, byteBuffer.Length);
            BytesWritten += byteBuffer.Length;
            ReportOutputClock(byteBuffer.Length, channels);
        }
    }

    private void ReportOutputClock(int bytes, int channels)
    {
        if (_nativeSource != null || IsFileRecorder || _testSink != null || _outputSampleRate <= 0)
        {
            return;
        }

        int frames = bytes / Math.Max(1, channels * sizeof(float));
        _outputClockFrames += frames;
        long now = _outputClock.ElapsedTicks;
        long interval = System.Diagnostics.Stopwatch.Frequency * 5;
        if (now - _outputClockLastTicks < interval)
        {
            return;
        }

        double seconds = _outputClock.Elapsed.TotalSeconds;
        if (seconds <= 0)
        {
            return;
        }

        double rate = _outputClockFrames / seconds;
        int bufferRate = _buffer?.WaveFormat.SampleRate ?? 48000;
        double ppm = (rate / Math.Max(1, bufferRate) - 1.0) * 1_000_000.0;
        _outputPpm = _outputPpmReady ? _outputPpm * 0.9 + ppm * 0.1 : ppm;
        _outputPpmReady = true;
        AudioClock.ReportOutputPpm(_outputPpm);
        _outputClock.Restart();
        _outputClockFrames = 0;
        _outputClockLastTicks = 0;
    }

    /// <summary>Mixes and emits one pump chunk.</summary>
    internal void RenderChunk(float[] mix, float[] temp, byte[] byteBuffer, int channels)
    {
        if (!MeasureAllocations)
        {
            RenderChunkCore(mix, temp, byteBuffer, channels);
            return;
        }

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
        bool anySoloSource = false;
        for (int r = 0; r < routes.Length; r++)
        {
            var candidate = routes[r].Source;
            if (candidate != null && (candidate.Solo || MixerGroupBus.Solo(candidate.GroupKey)))
            {
                anySoloSource = true;
                break;
            }
        }

        for (int r = 0; r < routes.Length; r++)
        {
            var route = routes[r];
            if (route.IsDisposed)
            {
                continue;
            }

            if (route.Source == null || route.Source.Muted ||
                MixerGroupBus.Muted(route.Source.GroupKey) ||
                (anySoloSource && !route.Source.Solo &&
                 !MixerGroupBus.Solo(route.Source.GroupKey)))
            {
                continue;
            }

            var subscription = route.Subscription;
            if (subscription == null)
            {
                continue;
            }

            int read;
            try
            {
                read = subscription.Read(temp);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AudioFaultLog.ReportEffectFault(route.Source.Title, ex);
                continue;
            }

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
            try
            {
                route.Process(temp.AsSpan(0, read), read / channels, channels);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AudioFaultLog.ReportEffectFault(route.Source.Title, ex);
                Array.Clear(temp, 0, read);
            }

            float gain = route.Gain * route.Source.Gain *
                         MixerGroupBus.Gain(route.Source.GroupKey);
            for (int i = 0; i < read; i++)
            {
                mix[i] += temp[i] * gain;
            }
        }

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
        }

        float outputGain = MixerGroupBus.Muted(GroupKey)
            ? 0f
            : Volume * MasterGain * MixerGroupBus.Gain(GroupKey);
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] *= outputGain;
        }

        if (Muted || MasterMuted ||
            (MixerGroupBus.AnyOutputSolo && !Solo && !MixerGroupBus.Solo(GroupKey)))
        {
            Array.Clear(mix, 0, mix.Length);
        }

        if (ClipGuardEnabled)
        {
            ApplyClipGuard(mix, mix.Length / 2);
        }
        else
        {
            ClipGuardActive = false;
        }

        for (int i = 0; i < mix.Length; i++)
        {
            if (!float.IsFinite(mix[i]))
            {
                mix[i] = 0f;
                clipped = true;
            }
            else if (mix[i] > 1f) { mix[i] = 1f; clipped = true; }
            else if (mix[i] < -1f) { mix[i] = -1f; clipped = true; }
        }

        Clipping = clipped;

        double sumLeft = 0;
        double sumRight = 0;
        int frames = Math.Max(1, mix.Length / 2);
        for (int i = 0; i + 1 < mix.Length; i += 2)
        {
            sumLeft += mix[i] * mix[i];
            sumRight += mix[i + 1] * mix[i + 1];
        }

        float meterLeft = Math.Min(1.0f, MathF.Sqrt((float)(sumLeft / frames)) * 3f);
        float meterRight = Math.Min(1.0f, MathF.Sqrt((float)(sumRight / frames)) * 3f);
        float dbLeft = (float)(20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumLeft / frames))));
        float dbRight = (float)(20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumRight / frames))));
        _meterLevelLeft = float.IsFinite(meterLeft) ? meterLeft : 0f;
        _meterLevelRight = float.IsFinite(meterRight) ? meterRight : 0f;
        _meterLevel = Math.Max(_meterLevelLeft, _meterLevelRight);
        _meterDbLeft = float.IsFinite(dbLeft) ? dbLeft : -60f;
        _meterDbRight = float.IsFinite(dbRight) ? dbRight : -60f;

        WriteSink(mix, byteBuffer, channels);
    }

    /// <summary>Total bytes handed to the render endpoint (diagnostics).</summary>
    public long BytesWritten { get; private set; }

    /// <summary>Native frames dropped to keep a lagging endpoint close to live.</summary>
    public long NativeSkippedFrames { get; private set; }

    /// <summary>Filtered clock error measured for this device, in ppm.</summary>
    public double ClockPpm => _outputPpm;
    public bool ClockPpmReady => _outputPpmReady;

    /// <summary>How much audio is queued in the render buffer right now, in ms.</summary>
    public double BufferedMs => _buffer?.BufferedDuration.TotalMilliseconds ?? 0;

    /// <summary>Playback state of the underlying endpoint ("Playing", "Stopped", …).</summary>
    public string PlaybackState => _recorder != null
        ? "Recording"
        : _wasapiOut?.PlaybackState.ToString() ?? "none";

    /// <summary>Rate the endpoint renders at.</summary>
    public int EndpointSampleRate => _outputSampleRate > 0 ? _outputSampleRate : 48000;

    /// <summary>True when the 48 kHz graph is being converted for the endpoint.</summary>
    public bool IsEndpointResampling => !IsNativePassthrough && EndpointSampleRate != AudioEngineConfig.SampleRate;

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // A handle that was just replaced can raise its stop event after the
        // new player has started; ignore anything that is not the live player.
        if (_stopRequested || !ReferenceEquals(sender, _wasapiOut))
        {
            return;
        }

        _running = false;
        string reason = e.Exception?.Message ?? "render endpoint stopped";
        Ruse.App.Diagnostics.AppLog.Warning(
            $"Output '{Title}' stopped: {reason} | state={_wasapiOut?.PlaybackState} " +
            $"format={_wasapiOut?.OutputWaveFormat?.SampleRate} Hz/" +
            $"{_wasapiOut?.OutputWaveFormat?.Channels} ch | " +
            $"native={IsNativePassthrough} source={_nativeSampleRate} Hz/" +
            $"{_nativeChannels} ch buffer={_buffer?.WaveFormat.SampleRate} Hz/" +
            $"{_buffer?.WaveFormat.Channels} ch");
        Faulted?.Invoke(this, reason);
    }

    public bool Stop()
    {
        if (!_running && _wasapiOut == null && _recorder == null && _pumpThread == null)
        {
            return true;
        }

        _stopRequested = true;
        _running = false;
        _watchdog?.Dispose();
        _watchdog = null;
        var pump = _pumpThread;
        if (pump != null && pump != Thread.CurrentThread)
        {
            if (!pump.Join(5000))
            {
                _pumpThread = pump;
                Ruse.App.Diagnostics.AppLog.Warning($"Output '{Title}' pump did not stop in time; keeping its resources.");
                return false;
            }
        }

        _pumpThread = null;
        _nativeSubscription?.Dispose();
        _nativeSubscription = null;
        DisposeRetiredNativeInstances();
        _recorderConverter = null;
        _recorderConverted.Clear();
        ClearEngineAudio();
        _outputResampler.Reset();
        _clipGuard.Reset();
        ClipGuardActive = false;

        var wasapi = _wasapiOut;
        _wasapiOut = null;
        if (wasapi != null)
        {
            wasapi.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                wasapi.Stop();
                wasapi.Dispose();
            }
            catch
            {
            }
        }


        if (_recorder != null)
        {
            bool finished = false;
            try
            {
                finished = _recorder.Stop(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _lastRecordingError = $"Could not finish the recording: {ex.Message}";
            }

            // Read these after Stop: encoders can still fail while flushing
            // their final blocks.
            _lastRecordedSeconds = _recorder.Duration;
            _lastRecordingError = _recorder.LastError ?? _lastRecordingError;

            if (finished)
            {
                _recorder.Dispose();
            }
            else
            {
                Ruse.App.Diagnostics.AppLog.Warning(
                    $"Recorder '{_recorder.Path}' is still finalizing; leaving it running in the background.");
            }

            _recorder = null;
        }

        _networkSender?.Dispose();
        _networkSender = null;
        if (IsNetworkOutput)
        {
            _testSink = null;
        }

        _buffer = null;
        _directProvider = null;
        _meterLevel = 0;
        _meterLevelLeft = 0;
        _meterLevelRight = 0;
        _meterDbLeft = -60f;
        _meterDbRight = -60f;
        return true;
    }

    public void Dispose()
    {
        if (!Stop())
        {
            // Retry teardown in the background instead of leaking the device
            // and routes forever when a driver thread is slow to exit.
            if (Interlocked.Exchange(ref _disposeRetryQueued, 1) == 0)
            {
                ThreadPool.QueueUserWorkItem(_ => RetryDispose());
                Ruse.App.Diagnostics.AppLog.Warning(
                    $"Output '{Title}' pump did not stop; retrying resource teardown in the background.");
            }

            return;
        }

        DisposeResources();
    }

    private void RetryDispose()
    {
        try
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                Thread.Sleep(1000);
                if (Stop())
                {
                    DisposeResources();
                    return;
                }
            }

            Ruse.App.Diagnostics.AppLog.Warning(
                $"Output '{Title}' pump never stopped; its resources remain for this process.");
        }
        finally
        {
            Interlocked.Exchange(ref _disposeRetryQueued, 0);
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _device?.Dispose();

        // Release every route's copy of the source audio.
        foreach (var route in _routeSnapshot)
        {
            route.Dispose();
        }

        _routeSnapshot = Array.Empty<Route>();
    }

    private sealed class DirectGraphProvider : IWaveProvider
    {
        private readonly OutputNode _owner;

        public DirectGraphProvider(OutputNode owner, WaveFormat format)
        {
            _owner = owner;
            WaveFormat = format;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            _owner.ReadDirect(buffer.AsSpan(offset, count));
            return count;
        }

        public int Read(Span<byte> buffer)
        {
            _owner.ReadDirect(buffer);
            return buffer.Length;
        }
    }

    public sealed class Route : IDisposable
    {
        private RouteChain _chain = new(
            Array.Empty<IAudioProcessor>(),
            Array.Empty<ParameterBinding>(),
            compensation: null,
            ownsInstances: true);

        public required SourceNode Source { get; init; }
        public float Gain { get; set; } = 1.0f;
        public bool Muted { get; set; }

        /// <summary>
        /// This route's private copy of the source's audio. Taking a copy per
        /// route is what lets one source feed several outputs: without it the
        /// outputs would take turns pulling from the same queue and each would
        /// hear only part of the signal.
        /// </summary>
        private readonly object _subscriptionGate = new();
        private volatile SourceNode.Subscription? _subscription;
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// Null after <see cref="Dispose"/>: a dead route must never lazily
        /// recreate a subscription, or it would steal audio from the source.
        /// </summary>
        public SourceNode.Subscription? Subscription
        {
            get
            {
                var subscription = _subscription;
                if (subscription != null)
                {
                    return subscription;
                }

                lock (_subscriptionGate)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return null;
                    }

                    return _subscription ??= Source.Subscribe();
                }
            }
        }

        public void Dispose()
        {
            SourceNode.Subscription? subscription;
            lock (_subscriptionGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                subscription = _subscription;
                _subscription = null;
            }

            RouteChain empty = new(
                Array.Empty<IAudioProcessor>(),
                Array.Empty<ParameterBinding>(),
                compensation: null,
                ownsInstances: true);
            RouteChain old = Interlocked.Exchange(ref _chain, empty);
            old.Retire(disposeInstances: true);
            subscription?.Dispose();
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
            get => Volatile.Read(ref _chain).Instances;
            set
            {
                var processors = value ?? Array.Empty<IAudioProcessor>();
                RouteChain replacement = new(
                    processors,
                    Array.Empty<ParameterBinding>(),
                    compensation: null,
                    ownsInstances: true);
                RouteChain old = Interlocked.Exchange(ref _chain, replacement);
                InherentLatencyFrames = SumLatency(processors);
                CompensationFrames = 0;
                old.Retire(disposeInstances: true);
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

            bool sameChain = Templates.Length == templateArray.Length;
            for (int i = 0; sameChain && i < templateArray.Length; i++)
            {
                sameChain = ReferenceEquals(Templates[i], templateArray[i]);
            }

            if (sameChain)
            {
                InherentLatencyFrames = latency;
                CompensationFrames = compensation;
                RouteChain current = Volatile.Read(ref _chain);
                if (current.CompensationFrames == compensation)
                {
                    return;
                }
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
            RouteChain replacement = new(
                instances,
                bindings,
                compensation > 0 ? new DelayLine(compensation, 2) : null,
                ownsInstances: true);
            Templates = templateArray;
            InherentLatencyFrames = latency;
            CompensationFrames = compensation;
            RouteChain old = Interlocked.Exchange(ref _chain, replacement);
            old.Retire(disposeInstances: true);
        }

        /// <summary>
        /// Runs the effect chain and any alignment delay on one block. Called on
        /// the output's render thread.
        /// </summary>
        public void Process(Span<float> block, int frames, int channels)
        {
            RouteChain? chain = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                RouteChain candidate = Volatile.Read(ref _chain);
                if (candidate.TryAcquire())
                {
                    chain = candidate;
                    break;
                }
            }

            if (chain == null)
            {
                return;
            }

            try
            {
                SyncParameters(chain.Bindings);

                var instances = chain.Instances;
                for (int i = 0; i < instances.Length; i++)
                {
                    // A null entry should never survive SetChain, but never take the
                    // audio thread down if a future chain builder publishes early.
                    instances[i]?.Process(block, frames);
                }

                chain.Compensation?.Process(block, channels);
            }
            finally
            {
                chain.Release();
            }
        }

        // Parameters are edited on the template, so mirror values that changed
        // across the block boundary — allocation-free and only on real edits.
        internal static void SyncParameters(ParameterBinding[] bindings)
        {
            for (int i = 0; i < bindings.Length; i++)
            {
                var binding = bindings[i];
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

        internal static IAudioProcessor Clone(IAudioProcessor template)
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

        internal static ParameterBinding[] BuildBindings(IAudioProcessor[] templates, IAudioProcessor[] instances)
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

        private sealed class RouteChain
        {
            private int _leases = 1;
            private int _retired;
            private int _disposed;
            private bool _ownsInstances;

            public RouteChain(
                IAudioProcessor[] instances,
                ParameterBinding[] bindings,
                DelayLine? compensation,
                bool ownsInstances)
            {
                Instances = instances;
                Bindings = bindings;
                Compensation = compensation;
                CompensationFrames = compensation?.Frames ?? 0;
                _ownsInstances = ownsInstances;
            }

            public IAudioProcessor[] Instances { get; }
            public ParameterBinding[] Bindings { get; }
            public DelayLine? Compensation { get; }
            public int CompensationFrames { get; }

            public bool TryAcquire()
            {
                Interlocked.Increment(ref _leases);
                if (Volatile.Read(ref _retired) == 0)
                {
                    return true;
                }

                Release();
                return false;
            }

            public void Release()
            {
                if (Interlocked.Decrement(ref _leases) == 0 &&
                    Volatile.Read(ref _retired) != 0 &&
                    _ownsInstances &&
                    Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    DisposeInstances(Instances);
                }
            }

            public void Retire(bool disposeInstances)
            {
                _ownsInstances = disposeInstances;
                if (Interlocked.Exchange(ref _retired, 1) != 0)
                {
                    return;
                }

                Release();
            }
        }

        internal sealed class ParameterBinding
        {
            public required AudioParameter Source { get; init; }
            public required AudioParameter Target { get; init; }
            public float Last { get; set; }
        }
    }
}
