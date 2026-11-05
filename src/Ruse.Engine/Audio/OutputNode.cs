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
    private float _meterLevelLeft;
    private float _meterLevelRight;
    private float _meterDbLeft = -60f;
    private float _meterDbRight = -60f;

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
    public double RecorderSilenceStopSeconds { get; set; }
    public double RecorderStartThresholdDb { get; set; } = -40;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; }
    public string Title { get; set; }
    public float Volume { get; set; } = 1.0f;
    public bool Solo { get; set; }
    public bool Muted { get; set; }

    /// <summary>Master clip guard for every output; set from settings.</summary>
    public static volatile bool ClipGuardEnabled = true;

    /// <summary>Try IAudioClient3 low-latency shared mode before normal shared mode.</summary>
    public static volatile bool LowLatencyWasapi = true;

    /// <summary>True while the guard is reducing gain on this output.</summary>
    public bool ClipGuardActive { get; internal set; }

    internal bool BypassClipGuardForTest { get; set; }

    private readonly ClipGuard _clipGuard = new();
    private const int SampleRate = 48000;

    /// <summary>True when this output records to a file rather than a device.</summary>
    public bool IsFileRecorder { get; private set; }

    /// <summary>
    /// True when the shared graph renderer owns this output's mix. Test sinks
    /// use the per-route renderer so engine-level self-tests stay valid.
    /// </summary>
    internal bool UseGraphEngine { get; set; } = true;

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
    private IAudioProcessor[] _nativeProcessors = Array.Empty<IAudioProcessor>();
    private bool _nativeFaultReported;

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
    internal Action? EngineWake
    {
        get => _engineWake;
        set => _engineWake = value;
    }

    private const int EngineRingSeconds = 1;
    private readonly FloatRingBuffer _engineBuffer = new(48000 * 2 * EngineRingSeconds);
    private readonly object _engineLock = new();

    internal int ElasticTargetFrames { get; set; }
    internal int ElasticVersion { get; set; } = -1;
    internal long ElasticUnderrunMark { get; set; }
    internal long ElasticLastAdjust { get; set; }

    /// <summary>Audio queued by the graph renderer for this output, in frames.</summary>
    internal double EngineBufferedFrames => _engineBuffer.AvailableToRead / 2.0;

    internal double EngineBufferedMs => EngineBufferedFrames * 1000.0 / 48000;
    public double EngineQueuePercent =>
        _engineBuffer.AvailableToRead / (48000.0 * 2 * EngineRingSeconds) * 100.0;

    internal void SubmitEngineBlock(ReadOnlySpan<float> block)
    {
        // Control-side clears are rare; never block the render thread behind one.
        if (!Monitor.TryEnter(_engineLock))
        {
            return;
        }

        try
        {
            _engineBuffer.Write(block);
        }
        finally
        {
            Monitor.Exit(_engineLock);
        }
    }

    internal void ClearEngineAudio()
    {
        lock (_engineLock)
        {
            _engineBuffer.Clear();
        }
    }

    internal bool ApplyClipGuard(float[] mix, int frames)
    {
        if (BypassClipGuardForTest)
        {
            ClipGuardActive = false;
            return false;
        }

        _clipGuard.Process(mix, frames);
        ClipGuardActive = _clipGuard.Engaged;
        return ClipGuardActive;
    }

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

    private volatile bool _stopRequested;

    /// <summary>Approximate endpoint latency, in ms.</summary>
    public double EstimatedLatencyMs
    {
        get
        {
            double queued = _buffer?.BufferedDuration.TotalMilliseconds ?? 0;
            double guard = _nativeSource == null && ClipGuardEnabled && !BypassClipGuardForTest
                ? _clipGuard.LatencySamples * 1000.0 / SampleRate
                : 0;
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
        // first render. The graph renderer reads each source once and fans it
        // out internally, so it does not need per-route copies.
        if (!UseGraphEngine)
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

    /// <summary>Creates an output that renders to an ASIO driver.</summary>
    public OutputNode(AsioDriverInfo driver)
    {
        _device = null!;
        DeviceId = AsioCatalog.IdFor(driver.Name);
        Title = driver.Name;
        _asioDriver = driver;
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

        if (_asioDriver != null)
        {
            int driverRate = _asioDriver.SampleRate;
            if (driverRate > 0 && driverRate != source.SampleRate)
            {
                reason = $"The ASIO driver is configured for {driverRate} Hz; the source is {source.SampleRate} Hz.";
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
        var chain = processors ?? Array.Empty<IAudioProcessor>();
        bool sameSource = ReferenceEquals(_nativeSource, source);
        _nativeProcessors = chain;
        if (!sameSource)
        {
            _nativeSubscription?.Dispose();
            _nativeSubscription = null;
            _nativeSource = source;
            _nativeSampleRate = source?.SampleRate ?? 0;
            _nativeChannels = source?.Channels ?? 0;
            _nativeFaultReported = false;
            if (source != null && source.SampleRate > 0)
            {
                _nativeSubscription = source.SubscribeNative();
            }
        }

        if (_nativeSampleRate > 0)
        {
            for (int i = 0; i < chain.Length; i++)
            {
                (chain[i] as INativeProcessor)?.ConfigureNative(_nativeSampleRate, 2);
            }
        }
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

        if (_wasapiOut != null || _asioEndpoint != null)
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
        }

        _outputSampleRate = IsFileRecorder ? recordRate : pumpRate;
        _stopRequested = false;
        _running = true;
        Underruns = 0;
        _renderedOnce = false;
        _startedAt = Environment.TickCount64;
        _lastUnderrunTick = 0;
        _underrunBurst = 0;
        _latencyDowngrades = 0;
        _latencyPenaltyMs = 0;
        ClearEngineAudio();
        _clipGuard.Reset();
        ClipGuardActive = false;
        if (IsNetworkOutput)
        {
            _networkSender?.Dispose();
            _networkSender = new NetworkPcmSender(_networkHost, _networkPort);
            _testSink = _networkSender.Send;
            _outputLatencyMs = 0;
        }

        try
        {
        if (IsFileRecorder)
        {
            _lastRecordedSeconds = 0;
            _lastRecordingError = null;
            try
            {
                _recorder = new AudioFileRecorder(
                    _recordingPath!, _recordingFormat, recordRate, recordChannels);
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
                _asioEndpoint.Start(_buffer, native ? pumpRate : 0);
                _outputLatencyMs = _asioEndpoint.LatencyMs;
                _lowLatency = true;
                _asioSampleRate = _asioEndpoint.SampleRate;
                _outputSampleRate = _asioEndpoint.SampleRate;

                if (native && _asioEndpoint.UsesResampler)
                {
                    Faulted?.Invoke(this,
                        $"ASIO driver '{_asioDriver.Name}' could not open at {pumpRate} Hz; native passthrough needs the driver rate to match the source.");
                    ResetFailedStart();
                    return;
                }

                if (!_asioEndpoint.Streaming)
                {
                    // No clock: the driver's host is not running, so the node
                    // would be silent. Say so instead of failing quietly.
                    Faulted?.Invoke(this,
                        $"ASIO driver '{_asioDriver.Name}' is not streaming (state {_asioEndpoint.State}). " +
                        "Its host application may not be running.");
                    ResetFailedStart();
                    return;
                }
            }
            else
            {
                // Ask WASAPI for a low-latency shared-mode stream via IAudioClient3.
                // If the device can't grant it, WasapiPlayer transparently falls back
                // to standard shared mode and reports the period it actually got.
                var builder = new WasapiPlayerBuilder()
                    .WithDevice(_device)
                    .WithSharedMode()
                    .WithEventSync();
                if (LowLatencyWasapi)
                {
                    builder = builder.WithLowLatency();
                }

                // Ask for the smallest period the endpoint actually supports
                // instead of assuming 3 ms everywhere.
                _wasapiOut = builder.WithLatency(ChooseWasapiLatencyMs()).Build();

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

                if (native && _wasapiOut.OutputWaveFormat.SampleRate != pumpRate)
                {
                    Faulted?.Invoke(this,
                        $"The device opened at {_wasapiOut.OutputWaveFormat.SampleRate} Hz instead of {pumpRate} Hz; native passthrough would be resampled.");
                    ResetFailedStart();
                    return;
                }
            }
        }

        RefreshRoutes();

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
        try
        {
            _asioEndpoint?.Dispose();
        }
        catch
        {
        }

        _asioEndpoint = null;
        _buffer = null;
    }

    private void PumpLoop(int sampleRate, int channels)
    {
        using var mmcss = MmcssScope.Enter();
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
                long due = (long)(clock.Elapsed.TotalSeconds * sampleRate);
                if (produced < due)
                {
                    RenderOrDrainChunk(mix, temp, byteBuffer, channels);
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
                        RenderOrDrainChunk(mix, temp, byteBuffer, channels);
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
                RenderOrDrainChunk(mix, temp, byteBuffer, channels);
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

                    RenderOrDrainChunk(mix, temp, byteBuffer, channels);
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
            double endpointMs;
            if (_asioEndpoint != null)
            {
                endpointMs = _asioEndpoint.SampleRate > 0 && _asioEndpoint.BufferSize > 0
                    ? _asioEndpoint.BufferSize * 1000.0 / _asioEndpoint.SampleRate
                    : _asioEndpoint.LatencyMs;
            }
            else
            {
                endpointMs = _outputLatencyMs;
            }

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

    /// <summary>Frames that had to be silence-padded because the source underran.</summary>
    public long ShortReadFrames { get; private set; }

    /// <summary>Times the device buffer was empty when the pump wanted to refill it.</summary>
    public long Underruns { get; private set; }

    private bool _renderedOnce;
    private long _startedAt;
    private long _lastUnderrunTick;
    private int _underrunBurst;
    private int _latencyDowngrades;
    private double _latencyPenaltyMs;

    /// <summary>Bytes allocated by the render thread while mixing (diagnostics).</summary>
    public long RenderAllocatedBytes { get; private set; }

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
        if (sourceChannels == channels)
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
                _nativeScratch = new float[outFrames * sourceChannels];
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
        for (int i = 0; i < processors.Length; i++)
        {
            processors[i].Process(mix.AsSpan(0, mix.Length), channels == 1 ? outFrames : outFrames);
        }

        if (Muted || MasterMuted || source.Muted)
        {
            Array.Clear(mix);
        }
        else
        {
            float gain = Volume * MasterGain * source.Gain;
            if (Math.Abs(gain - 1f) > 1e-6f)
            {
                for (int i = 0; i < mix.Length; i++)
                {
                    mix[i] *= gain;
                }
            }
        }

        ClipGuardActive = false;
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
        int read = _engineBuffer.Read(mix);
        if (read < mix.Length)
        {
            Array.Clear(mix, read, mix.Length - read);
            if (_renderedOnce)
            {
                Underruns++;
                RegisterUnderrun();
            }

            ShortReadFrames += (mix.Length - read) / channels;
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
        Ruse.App.Diagnostics.AppLog.Info(
            $"Output '{Title}' is underrunning; adding 4 ms of latency cushion " +
            $"(total +{_latencyPenaltyMs:0} ms, step {_latencyDowngrades}/3).");
    }

    private void WriteSink(float[] mix, byte[] byteBuffer, int channels)
    {
        if (_recorder != null)
        {
            if (_recorderConverter == null)
            {
                _recorder.Write(mix, mix.Length);
            }
            else
            {
                _recorderConverted.Clear();
                _recorderConverter.Convert(mix.AsSpan(0, mix.Length), _recorderConverted);
                _recorder.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_recorderConverted));
            }
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

        float outputGain = Volume * MasterGain;
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] *= outputGain;
        }

        if (Muted || MasterMuted)
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

    /// <summary>How much audio is queued in the render buffer right now, in ms.</summary>
    public double BufferedMs => _buffer?.BufferedDuration.TotalMilliseconds ?? 0;

    /// <summary>Playback state of the underlying endpoint ("Playing", "Stopped", …).</summary>
    public string PlaybackState => _recorder != null
        ? "Recording"
        : _wasapiOut?.PlaybackState.ToString() ?? (_asioEndpoint?.State ?? "none");

    /// <summary>True when the ASIO driver has delivered at least one buffer.</summary>
    public bool AsioStreaming => _asioEndpoint?.Streaming ?? false;

    /// <summary>Buffer callbacks delivered by the ASIO driver (diagnostics).</summary>
    public long AsioCallbackCount => _asioEndpoint?.CallbackCount ?? 0;

    /// <summary>Rate the endpoint renders at: 48 kHz for WASAPI, the driver's rate for ASIO.</summary>
    public int EndpointSampleRate => _outputSampleRate > 0 ? _outputSampleRate : 48000;

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

    public void Stop()
    {
        if (!_running && _wasapiOut == null && _recorder == null && _asioEndpoint == null && _pumpThread == null)
        {
            return;
        }

        _stopRequested = true;
        _running = false;
        var pump = _pumpThread;
        if (pump != null && pump != Thread.CurrentThread)
        {
            if (!pump.Join(5000))
            {
                _pumpThread = pump;
                Ruse.App.Diagnostics.AppLog.Warning($"Output '{Title}' pump did not stop in time; keeping its resources.");
                return;
            }
        }

        _pumpThread = null;
        _nativeSubscription?.Dispose();
        _nativeSubscription = null;
        _recorderConverter = null;
        _recorderConverted.Clear();
        ClearEngineAudio();
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

        _networkSender?.Dispose();
        _networkSender = null;
        if (IsNetworkOutput)
        {
            _testSink = null;
        }

        _buffer = null;
        _meterLevel = 0;
        _meterLevelLeft = 0;
        _meterLevelRight = 0;
        _meterDbLeft = -60f;
        _meterDbRight = -60f;
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
                int latency = SumLatency(processors);
                IAudioProcessor[] old;
                lock (_chainLock)
                {
                    old = _instances;
                    _instances = processors;
                    _bindings = Array.Empty<ParameterBinding>();
                    _compensation = null;
                    CompensationFrames = 0;
                    InherentLatencyFrames = latency;
                }

                DisposeInstances(old);
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
            IAudioProcessor[] old;
            lock (_chainLock)
            {
                old = _instances;
                Templates = templateArray;
                _instances = instances;
                _bindings = bindings;
                InherentLatencyFrames = latency;
                CompensationFrames = compensation;
                _compensation = delay;
            }

            DisposeInstances(old);
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
        // across the block boundary — allocation-free and only on real edits.
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
