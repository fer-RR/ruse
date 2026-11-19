using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>Native graph host; all mutations run on the WPF dispatcher.</summary>
internal sealed partial class WebShellController : IDisposable
{
    private readonly WebMainWindow _window;
    private readonly AppSettings _settings;
    private readonly UpdateService _updates;

    internal AppSettings UserSettings => _settings;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private AudioGraph _graph = new();
    private readonly Dictionary<string, NodeEntry> _nodes = new();
    private readonly List<LinkEntry> _links = new();
    // Rebuilt lazily for telemetry and invalidated on link add/remove/clear.
    private LinkIndex? _cachedLinkIndex;
    // Reused per telemetry tick; Post serializes synchronously on the UI thread.
    private readonly TelemetryDto _telemetry = new();
    private readonly Dictionary<string, float> _telemetrySourceLevels = new();
    private readonly Dictionary<string, bool> _telemetrySourceClipping = new();
    private readonly Dictionary<string, bool> _telemetryOutputClipping = new();
    private readonly Dictionary<string, float> _telemetryModifierLevels = new();
    private readonly HashSet<string> _telemetryVisiting = new();
    private readonly HashSet<string> _telemetryClippingUpstream = new();
    private readonly HashSet<string> _telemetryClippingDownstream = new();
    private readonly HashSet<string> _telemetrySeenIds = new();
    private readonly List<string> _telemetryStaleIds = new();
    private readonly Dictionary<string, long> _nodeClipHoldUntil = new();
    private readonly Dictionary<string, TelemetryTextCache> _telemetryText = new();
    private readonly Dictionary<string, long> _spectrogramCursors = new();
    private readonly Dictionary<string, string> _nativeFallbackSources = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _telemetryTimer;
    private sealed class TelemetryTextCache
    {
        public string? Transcript;
        public string? PartialTranscript;
        public string? TranslatedTranscript;
        public string? TranslatedPartial;
    }
    private int _clockRefreshTick;
    private readonly DispatcherTimer _appsTimer;
    private readonly DispatcherTimer _devicesTimer;
    private readonly DispatcherTimer _updateTimer;
    private CoreWebView2? _core;
    private WebView2? _webView;
    private string _webView2Version = "";
    private CatalogDto _catalog = new();
    private int _graphVersion;
    private bool _running;
    private bool _micMuted;
    private bool _disposed;
    private bool _shutdownSaved;
    private const long ClipHoldMs = 2000;
    private bool _autoStartDone;
    private bool _uiReady;
    private bool _startupLoaded;
    private bool _uiPublished;
    private readonly TaskCompletionSource<bool> _uiReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private double _startupProgress = 0.08;
    private string _startupLabel = "Starting Ruse";
    private bool _postFailureLogged;
    private long _lastTelemetryPublish;
    private string? _lastGpuFallbackReason;
    private string _statusText = "Native audio engine ready.";
    private CancellationTokenSource? _modelDownloadCancel;
    private TranscriberModelProgress? _modelDownload;
    private long _lastModelPublish;
    private long _lastModelStatus;
    private CancellationTokenSource? _translationDownloadCancel;
    private TranslationModelProgress? _translationDownload;
    private long _lastTranslationPublish;
    private long _lastTranslationStatus;
    private CancellationTokenSource? _ttsDownloadCancel;
    private TtsModelProgress? _ttsDownload;
    private long _lastTtsPublish;
    private VoskModelProgress? _voskDownload;
    private long _lastVoskPublish;
    private CancellationTokenSource? _voskDownloadCancel;
    private long _lastBargeInTick;
    private readonly HashSet<int> _speechTaps = new();
    private Action<bool?>? _trayDecision;

    public WebShellController(WebMainWindow window)
    {
        _window = window;
        _settings = AppSettings.Load();
        RuseFolders.EnsureAll();
        ModelStorage.Ensure();
        EnsureDefaultMediaFolders();
        StreamingAsrOptions.Enabled = _settings.StreamingAsrEnabled;
        SpeechActivityHub.SpeechStarted += OnSpeechStarted;
        SpeechActivityHub.SpeechEnded += OnSpeechEnded;
        FrozenProxyStore.Initialize();
        _graph.AutoRun = false;
        _settings.Theme = _settings.Theme switch
        {
            "system" => "dark",
            "ocean" => "blueprint",
            "ember" => "studio74",
            "midnightstudy" or "midnight" or "deepdive" or "glass" => "dark",
            var theme => theme,
        };
        MixerGroupBus.Configure(_settings.MixerGroups);
        TranslationSettings.Update(_settings.TranslationGlossary, _settings.TranslationMemoryEnabled);
        ConfigureHistoryLimits();
        _updates = new UpdateService(this);
        ModelRuntime.UseGpu = _settings.UseGpu;
        Vst3ProcessorFactory.UseIsolation = _settings.IsolateVst3;
        OutputNode.AggressiveCushion = _settings.AggressiveOutputCushion;
        OutputNode.ClipGuardEnabled = _settings.ClipGuard;
        OutputNode.LowLatencyWasapi = _settings.LowLatencyWasapi;
        ConfigureAudioEngine();
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _telemetryTimer.Tick += (_, _) =>
        {
            CheckArmedRecorders();
            CheckRecorderSilence();
            if (++_clockRefreshTick % 10 == 0)
            {
                _graph.UpdateSourceClocks();
            }

            PublishTelemetry();
            FlushMidiParameterChanges();
            FlushControlParameterChanges();
            FlushTimelineParameterChanges();
            PublishTimelinePlayhead();
        };
        _appsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_settings.FollowPlayingApps ? 750 : 4000),
        };
        _appsTimer.Tick += (_, _) => RefreshApps();
        _devicesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _devicesTimer.Tick += (_, _) =>
        {
            _devicesTimer.Stop();
            RefreshDevicesIfChanged(force: true);
        };
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _updateTimer.Tick += (_, _) => _ = _updates.MaybeAutoCheckAsync();
    }

    public async Task InitializeAsync(WebView2 webView)
    {
        string userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ruse", "WebView2");
        Directory.CreateDirectory(userData);
        var environment = await CoreWebView2Environment.CreateAsync(null, userData);
        await webView.EnsureCoreWebView2Async(environment);
        _core = webView.CoreWebView2;
        _webView = webView;
        _webView2Version = environment.BrowserVersionString;
        ApplyUiScale();

        var settings = _core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif

        _core.WebMessageReceived += OnWebMessageReceived;
        _core.ProcessFailed += OnCoreProcessFailed;
        _core.NavigationStarting += OnCoreNavigationStarting;
        _core.NewWindowRequested += OnCoreNewWindowRequested;

        string webRoot = Path.Combine(AppContext.BaseDirectory, "WebUi");
        string indexPath = Path.Combine(webRoot, "index.html");
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException("The Web UI assets are missing. Run 'npm run build' in src/Ruse.App/WebUi.");
        }

        _core.SetVirtualHostNameToFolderMapping(
            "ui.ruse", webRoot, CoreWebView2HostResourceAccessKind.Allow);

        RestoreWindowBounds();
        long buildStamp = File.GetLastWriteTimeUtc(indexPath).Ticks;
        _core.Navigate($"https://ui.ruse/index.html?v={buildStamp}");
        _telemetryTimer.Start();
        _appsTimer.Start();

        PostStartupProgress(0.12, "Starting Ruse");
        await Task.WhenAny(_uiReadySignal.Task, Task.Delay(1000));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        _ = Task.Run(ModelRuntime.ConfigureNativeLibraries);

        PostStartupProgress(0.22, "Loading library");
        SubgraphLibrary.Load();
        FrameTemplateLibrary.Load();
        PostStartupProgress(0.4, "Scanning audio devices");
        RefreshCatalog();
        RegisterDeviceNotifications();
        PostStartupProgress(0.62, "Restoring session");
        if (App.LiveDemoMode)
        {
            BuildLiveGraph(App.LiveDemoProcessId);
        }
        else if (App.DemoMode)
        {
            BuildDemoGraph();
        }
        else if (App.ControlTestMode)
        {
            if (File.Exists(AppSettings.SessionPath))
            {
                TryLoadSession();
            }
            else
            {
                BuildDemoGraph();
            }
        }
        else if (_settings.AutoLoadSession)
        {
            TryLoadSession();
        }

        PostStartupProgress(0.82, "Starting MIDI");
        InitializeMidi();
        _startupLoaded = true;
        CompleteUiReady();
        await StartControlServerAsync();
    }

    private void PostStartupProgress(double progress, string label)
    {
        _startupProgress = Math.Clamp(progress, 0, 1);
        _startupLabel = label;
        if (_uiReady)
        {
            Post("startup-progress", new { progress = _startupProgress, label });
        }
    }

    private void CompleteUiReady()
    {
        if (!_uiReady || !_startupLoaded || _uiPublished)
        {
            return;
        }

        _uiPublished = true;
        PublishAll();
        PostStartupProgress(1, "Ready");
        Post("startup-complete", new { });
        WarnIfHistoryLimited();
        _updateTimer.Start();
        _ = _updates.MaybeAutoCheckAsync();
        if (!App.DemoMode && !App.LiveDemoMode && _settings.ShowWelcomeScreen)
        {
            Post("splash", new
            {
                firstRun = !_settings.OnboardingCompleted && _nodes.Count == 0,
            });
        }

        if (!_autoStartDone && _settings.StartRoutingOnLaunch && _nodes.Count > 0 && !App.DemoMode)
        {
            _autoStartDone = true;
            Dispatcher.CurrentDispatcher.BeginInvoke(StartRouting, DispatcherPriority.ApplicationIdle);
        }
    }

    private void OnCoreProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) =>
        ShowError("The WebView2 UI process failed. Restart Ruse to recover the interface.");

    private void OnCoreNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!args.Uri.StartsWith("https://ui.ruse/", StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
        }
    }

    private void OnCoreNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        args.Handled = true;

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || !IsTrustedUiSource(e.Source))
        {
            return;
        }

        try
        {
            var message = JsonSerializer.Deserialize<BridgeMessage>(e.WebMessageAsJson, _json);
            if (message?.Type == "file-drop")
            {
                var paths = e.AdditionalObjects
                    .OfType<CoreWebView2File>()
                    .Select(file => file.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .ToList();
                if (paths.Count > 0)
                {
                    AddFilePlayer(Number(message, "x", 80), Number(message, "y", 120), paths[0], String(message, "connectTo"));
                }

                return;
            }

            if (message?.Type == "timeline-file-drop")
            {
                var paths = e.AdditionalObjects
                    .OfType<CoreWebView2File>()
                    .Select(file => file.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .ToList();
                if (paths.Count > 0)
                {
                    AddTimelineClipPath(String(message, "trackId"), paths[0], (long)Number(message, "startSample"));
                }

                return;
            }

            if (message?.Type == "sample-drop")
            {
                var paths = e.AdditionalObjects
                    .OfType<CoreWebView2File>()
                    .Select(file => file.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .ToList();
                if (paths.Count > 0)
                {
                    ImportSamples(paths);
                }

                return;
            }

            if (message != null)
            {
                HandleMessage(message);
            }
        }
        catch (Exception ex)
        {
            ShowError("The UI command failed: " + ex.Message, ex);
        }
    }

    private static bool IsTrustedUiSource(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "ui.ruse", StringComparison.OrdinalIgnoreCase);

    private void HandleMessage(BridgeMessage message)
    {
        switch (message.Type)
        {
            case "ready":
                _uiReady = true;
                _uiReadySignal.TrySetResult(true);
                _spectrogramCursors.Clear();
                if (!_startupLoaded)
                {
                    PostStartupProgress(_startupProgress, _startupLabel);
                }
                CompleteUiReady();
                break;
            case "refresh": RefreshCatalog(); break;
            case "samples-open-folder": OpenSamplesFolder(); break;
            case "samples-import": ChooseSamples(); break;
            case "samples-refresh": RefreshSamples(); Post("catalog", _catalog); SetStatus("Samples refreshed."); break;
            case "sample-import-path": ImportSamplePath(String(message, "path")); break;
            case "media-scan": RefreshMediaLibrary(); break;
            case "media-add-folder": AddMediaFolder(); break;
            case "media-remove-folder": RemoveMediaFolder(String(message, "path")); break;
            case "media-open-folder": OpenMediaFolder(String(message, "path")); break;
            case "media-rename": RenameMediaFile(String(message, "path"), String(message, "name")); break;
            case "media-reveal": RevealMediaFile(String(message, "path")); break;
            case "media-add-to-timeline": AddMediaFileToTimeline(String(message, "path")); break;
            case "media-reorder-folders": ReorderMediaFolders(message); break;
            case "media-waveform": LoadMediaWaveform(String(message, "path")); break;
            case "update-status": _updates.PublishState(); break;
            case "check-updates": _ = _updates.CheckAsync(); break;
            case "download-update": _ = _updates.DownloadAsync(); break;
            case "apply-update": _updates.Apply(); break;
            case "audio-optimizer-status": PostAudioOptimizerState(); break;
            case "timeline-open": OpenTimeline(); break;
            case "timeline-add-track": AddTimelineTrack(); break;
            case "timeline-add-clip": AddTimelineClip(String(message, "trackId")); break;
            case "timeline-add-clip-path": AddTimelineClipPath(String(message, "trackId"), String(message, "path"), (long)Number(message, "startSample")); break;
            case "timeline-add-clip-paths": AddTimelineClipPaths(String(message, "trackId"), Strings(message, "paths"), OptionalLong(message, "startSample")); break;
            case "timeline-add-generator": AddTimelineGenerator(String(message, "nodeId"), String(message, "trackId"), (long)Number(message, "startSample", 0)); break;
            case "timeline-remove-track": RemoveTimelineTrack(String(message, "trackId")); break;
            case "timeline-show-node": ShowTimelineTrackNode(String(message, "trackId")); break;
            case "timeline-track-update": UpdateTimelineTrack(String(message, "trackId"), (float)Number(message, "gain", 1), Bool(message, "muted", false), Bool(message, "solo", false)); break;
            case "timeline-track-rename": RenameTimelineTrack(String(message, "trackId"), String(message, "name")); break;
            case "timeline-remove-clip": RemoveTimelineClip(String(message, "trackId"), String(message, "clipId")); break;
            case "timeline-move-clip": MoveTimelineClip(String(message, "trackId"), String(message, "clipId"), (long)Number(message, "startSample")); break;
            case "timeline-move-clip-track": MoveTimelineClipToTrack(String(message, "fromTrackId"), String(message, "toTrackId"), String(message, "clipId"), (long)Number(message, "startSample")); break;
            case "timeline-update-clip": UpdateTimelineClip(message); break;
            case "timeline-stretch-clip": StretchTimelineClip(message); break;
            case "timeline-split-clip": SplitTimelineClip(String(message, "trackId"), String(message, "clipId"), (long)Number(message, "sample", 0)); break;
            case "timeline-duplicate-clip": DuplicateTimelineClip(String(message, "trackId"), String(message, "clipId")); break;
            case "timeline-crossfade-clip": CrossfadeTimelineClip(String(message, "trackId"), String(message, "clipId")); break;
            case "timeline-automation-add": AddTimelineAutomationLane(message); break;
            case "timeline-automation-points": SetTimelineAutomationPoints(message); break;
            case "timeline-automation-remove": RemoveTimelineAutomationLane(String(message, "trackId"), String(message, "laneId")); break;
            case "timeline-undo": TimelineUndo(); break;
            case "timeline-redo": TimelineRedo(); break;
            case "timeline-metronome": SetTimelineMetronome(Bool(message, "value")); break;
            case "timeline-count-in": TimelineCountIn(); break;
            case "timeline-count-in-beats": SetTimelineCountInBeats((int)Number(message, "value", 4)); break;
            case "timeline-play": TimelinePlay(); break;
            case "timeline-pause": TimelinePause(); break;
            case "timeline-stop": TimelineStop(); break;
            case "timeline-record": ToggleTimelineRecording(); break;
            case "timeline-seek": TimelineSeek((long)Number(message, "sample")); break;
            case "timeline-loop": TimelineSetLoop((long?)Number(message, "startSample", -1), (long?)Number(message, "endSample", -1)); break;
            case "timeline-tempo": TimelineSetTempo(Number(message, "tempo", 120)); break;
            case "timeline-time-signature": TimelineSetTimeSignature((int)Number(message, "numerator", 4), (int)Number(message, "denominator", 4)); break;
            case "timeline-marker-add": AddTimelineMarker(message); break;
            case "timeline-marker-update": UpdateTimelineMarker(message); break;
            case "timeline-marker-remove": RemoveTimelineMarker(String(message, "id")); break;
            case "timeline-copy-clips": SnapshotTimelineClips(message); break;
            case "timeline-paste-clipboard": PasteTimelineClips(message); break;
            case "timeline-normalize-clip": NormalizeTimelineClip(String(message, "trackId"), String(message, "clipId")); break;
            case "timeline-reverse-clip": ReverseTimelineClip(String(message, "trackId"), String(message, "clipId")); break;
            case "timeline-consolidate": ConsolidateTimeline(message); break;
            case "timeline-bounce": BounceTimeline(); break;
            case "timeline-bounce-range": BounceTimelineRange(); break;
            case "timeline-bounce-cancel": CancelTimelineBounce(); break;
            case "timeline-freeze": FreezeTimeline(); break;
            case "audio-optimizer-run": StartAudioOptimizer(); break;
            case "add": AddFromMessage(message); break;
            case "route-check-test-tone": AddRouteCheckTestTone(); break;
            case "add-file-player": AddFilePlayer(Number(message, "x", 80), Number(message, "y", 120), String(message, "path"), String(message, "connectTo")); break;
            case "file-player-file": ChooseFilePlayerFile(String(message, "id")); break;
            case "file-player-loop": SetFilePlayerLoop(String(message, "id"), Bool(message, "value")); break;
            case "file-player-analyze": AnalyzeFilePlayer(String(message, "id")); break;
            case "file-player-speed": SetFilePlayerSpeed(String(message, "id"), (float)Number(message, "value", 1)); break;
            case "file-player-output": SetFilePlayerOutput(String(message, "id"), String(message, "outputId")); break;
            case "file-player-start": StartFilePlayer(String(message, "id")); break;
            case "file-player-stop": StopFilePlayer(String(message, "id")); break;
            case "file-player-pause": PauseFilePlayer(String(message, "id")); break;
            case "file-player-resume": ResumeFilePlayer(String(message, "id")); break;
            case "file-player-seek": SeekFilePlayer(String(message, "id"), Number(message, "seconds")); break;
            case "file-player-restart": RestartFilePlayer(String(message, "id")); break;
            case "first-route": CreateFirstRoute(message); break;
            case "onboarding-complete": CompleteOnboarding(); break;
            case "show-splash": Post("splash", new { manual = true }); break;
            case "open-sound-settings": OpenSoundSettings(); break;
            case "create-desktop-shortcut": CreateDesktopShortcut(); break;
            case "move": MoveNode(String(message, "id"), Number(message, "x"), Number(message, "y")); break;
            case "move-many": MoveNodes(message); break;
            case "duplicate": DuplicateNode(String(message, "id")); break;
            case "undo": Undo(); break;
            case "redo": Redo(); break;
            case "remove": RemoveNode(String(message, "id")); break;
            case "connect": ConnectNodes(String(message, "from"), String(message, "to"), String(message, "kind"), (float)Number(message, "gain", 1), Bool(message, "enabled", true)); break;
            case "disconnect": DisconnectLink(String(message, "id")); break;
            case "link-gain": SetLinkGain(String(message, "id"), (float)Number(message, "value", 1)); break;
            case "link-pan": SetLinkPan(String(message, "id"), (float)Number(message, "value", 0)); break;
            case "link-solo": SetLinkSolo(String(message, "id"), Bool(message, "value")); break;
            case "link-bus": SetLinkBus(String(message, "id"), Bool(message, "value")); break;
            case "mixer-settings": SetMixerSettings(String(message, "id"), (float)Number(message, "width", 1), Bool(message, "autoGain"), (float)Number(message, "crossfade", 0)); break;
            case "mixer-meter-reset": ResetMixerMeter(String(message, "id")); break;
            case "link-polarity": SetLinkPolarity(String(message, "id"), Bool(message, "value")); break;
            case "link-delay": SetLinkDelay(String(message, "id"), (float)Number(message, "value", 0)); break;
            case "link-enabled": SetLinkEnabled(String(message, "id"), Bool(message, "value")); break;
            case "link-native": SetLinkNative(String(message, "id"), Bool(message, "value")); break;
            case "link-native-cancel-restore": CancelNativeRestore(String(message, "id")); break;
            case "disconnect-node": DisconnectNode(String(message, "id")); break;
            case "frames": SetFrames(Payload<FramesPayload>(message)); break;
            case "save-frame-template": SaveFrameTemplate(Payload<FrameRefPayload>(message)); break;
            case "insert-template": InsertFrameTemplate(Payload<TemplateInsertPayload>(message)); break;
            case "template-delete": DeleteFrameTemplate(String(message, "name")); break;
            case "gain": SetNodeGain(String(message, "id"), (float)Number(message, "value", 1)); break;
            case "mute": SetNodeMute(String(message, "id"), Bool(message, "value")); break;
            case "solo": SetNodeSolo(String(message, "id"), Bool(message, "value")); break;
            case "retarget-app": RetargetApplication(String(message, "id"), (uint)Number(message, "processId"), String(message, "processName")); break;
            case "node-color": SetNodeColor(String(message, "id"), String(message, "color")); break;
            case "group-gain": SetGroupGain(String(message, "key"), (float)Number(message, "value", 1)); break;
            case "group-mute": SetGroupMuted(String(message, "key"), Bool(message, "value")); break;
            case "group-solo": SetGroupSolo(String(message, "key"), Bool(message, "value")); break;
            case "mixer-group": GroupMixerNodes(String(message, "from"), String(message, "to")); break;
            case "mixer-ungroup": UngroupMixerGroup(String(message, "groupId")); break;
            case "midi-learn": StartMidiLearn(String(message, "id"), (int)Number(message, "processor"), (int)Number(message, "parameter")); break;
            case "midi-unbind": ClearMidiBinding(String(message, "id"), (int)Number(message, "processor"), (int)Number(message, "parameter")); break;
            case "midi-refresh": _midi.RefreshDevices(); PublishSettings(); break;
            case "param": SetParameter(message); break;
            case "effect-enabled": SetEffectEnabled(String(message, "id"), Bool(message, "value")); break;
            case "parallel": SetModifierParallel(String(message, "id"), Bool(message, "value")); break;
            case "send": SetModifierSend(String(message, "id"), (float)Number(message, "value", 0.5)); break;
            case "tone": SetTone(String(message, "id"), (float)Number(message, "frequency", 1000), String(message, "waveform"), (float)Number(message, "level", -18)); break;
            case "metronome": SetNodeMetronome(String(message, "id"), (float)Number(message, "bpm", 120), (int)Number(message, "beats", 4)); break;
            case "node-size": SetNodeWidth(String(message, "id"), Number(message, "width", 268)); break;
            case "synth": SetSynth(message); break;
            case "ir-file": ChooseImpulseResponse(String(message, "id")); break;
            case "ir-clear": ClearImpulseResponse(String(message, "id")); break;
            case "noise-profile-file": ChooseNoiseProfile(String(message, "id")); break;
            case "noise-profile-calibrate": StartNoiseCalibration(String(message, "id")); break;
            case "noise-profile-calibrate-cancel": CancelNoiseCalibration(String(message, "id")); break;
            case "noise-profile-clear": ClearNoiseProfile(String(message, "id")); break;
            case "loudness-reset": ResetLoudness(String(message, "id")); break;
            case "db-meter-orientation": SetDbMeterOrientation(String(message, "id"), Bool(message, "vertical")); break;
            case "transcriber-model": SetTranscriberModel(String(message, "id"), String(message, "model")); break;
            case "transcriber-download": DownloadTranscriberModel(String(message, "id")); break;
            case "transcriber-download-cancel": CancelTranscriberDownload(); break;
            case "transcriber-delete": DeleteTranscriberModel(String(message, "id")); break;
            case "transcriber-clear": ClearTranscription(String(message, "id")); break;
            case "transcriber-restart": RestartTranscription(String(message, "id")); break;
            case "transcriber-export": ExportTranscription(String(message, "id")); break;
            case "transcriber-open-folder": OpenTranscriberModelsFolder(); break;
            case "translator-target": SetTranslatorTarget(String(message, "id"), String(message, "target")); break;
            case "translator-model": SetTranslatorModel(String(message, "id"), String(message, "model")); break;
            case "translator-download": DownloadTranslationModel(String(message, "id")); break;
            case "translator-download-cancel": CancelTranslationDownload(); break;
            case "translator-delete": DeleteTranslationModel(String(message, "id")); break;
            case "opus-download": DownloadOpusTranslationModel(String(message, "id")); break;
            case "opus-delete": DeleteOpusTranslationModel(String(message, "id")); break;
            case "translator-clear": ClearTranslation(String(message, "id")); break;
            case "translator-restart": RestartTranslation(String(message, "id")); break;
            case "translator-export": ExportTranslation(String(message, "id")); break;
            case "translator-open-folder": OpenTranslationModelsFolder(); break;
            case "tts-model": SetTtsModel(String(message, "id"), String(message, "model")); break;
            case "tts-download": DownloadTtsModel(String(message, "id")); break;
            case "tts-download-cancel": CancelTtsDownload(); break;
            case "tts-delete": DeleteTtsModel(String(message, "id")); break;
            case "tts-open-folder": OpenTtsModelsFolder(); break;
            case "tts-text": SetTtsText(String(message, "id"), String(message, "text")); break;
            case "tts-source": SetTtsSource(String(message, "id"), String(message, "source")); break;
            case "tts-open-text": ChooseTtsTextFile(String(message, "id")); break;
            case "tts-speed": SetTtsSpeed(String(message, "id"), (float)Number(message, "value", 1)); break;
            case "tts-voice": SetTtsVoice(String(message, "id"), (int)Number(message, "value", 0)); break;
            case "tts-speak": SpeakTts(String(message, "id")); break;
            case "tts-stop": StopTts(String(message, "id")); break;
            case "tts-jump-latest": JumpTtsToLatest(String(message, "id")); break;
            case "vosk-download": DownloadVoskModel(); break;
            case "vosk-delete": DeleteVoskModel(); break;
            case "network-input-port": SetNetworkInputPort(String(message, "id"), (int)Number(message, "value", NetworkPcm.DefaultPort)); break;
            case "network-output-host": SetNetworkOutputHost(String(message, "id"), String(message, "value")); break;
            case "network-output-port": SetNetworkOutputPort(String(message, "id"), (int)Number(message, "value", NetworkPcm.DefaultPort)); break;
            case "vst3-editor": SetVst3Editor(String(message, "id"), Bool(message, "open")); break;
            case "vst3-rescan": RescanVst3Plugins(); break;
            case "rename": RenameNode(String(message, "id"), String(message, "title")); break;
            case "freeze-effects": FreezeSourceEffects(String(message, "id")); break;
            case "transport":
                if (Bool(message, "running")) StartRouting(); else StopRouting();
                break;
            case "retry-routing":
                StartRouting();
                break;
            case "master-gain":
                OutputNode.MasterGain = (float)Number(message, "value", 1);
                break;
            case "master-mute":
                OutputNode.MasterMuted = Bool(message, "value");
                PublishTelemetry(force: true);
                break;
            case "mic-mute": SetMicrophoneMute(Bool(message, "value")); break;
            case "clear":
                ClearGraph();
                ClearHistory();
                DeleteSession();
                CleanupUnreferencedFrozenProxies();
                SetStatus("Graph cleared.");
                PublishGraph();
                break;
            case "save-preset": SavePresetDialog(); break;
            case "load-preset": LoadPresetDialog(); break;
            case "recorder-file": ChooseRecorderFile(String(message, "id"), String(message, "format", "Wav24")); break;
            case "recorder-open-folder": OpenRecorderFolder(String(message, "id")); break;
            case "recorder-format": SetRecorderFormat(String(message, "id"), String(message, "format", "Wav24")); break;
            case "recorder-rate": SetRecorderSampleRate(String(message, "id"), (int)Number(message, "value", 48000)); break;
            case "recorder-mode": SetRecorderMode(String(message, "id"), String(message, "mode", "Off")); break;
            case "recorder-autosave": SetRecorderAutoSave(String(message, "id"), Bool(message, "value")); break;
            case "recorder-save-location": SetRecorderSaveLocation(String(message, "id"), Bool(message, "value")); break;
            case "recorder-silence": SetRecorderSilence(String(message, "id"), Number(message, "value")); break;
            case "recorder-threshold": SetRecorderThreshold(String(message, "id"), Number(message, "value")); break;
            case "recorder-stop": StopRecorder(String(message, "id")); break;
            case "chain-save": SaveChain(message); break;
            case "chain-delete": DeleteChain(String(message, "name")); break;
            case "chain-open": OpenChainForNode(String(message, "id")); break;
            case "settings": ApplySettings(Payload<SettingsDto>(message) ?? new SettingsDto()); break;
            case "close-to-tray-result":
                ResolveTrayDecision(Bool(message, "cancel") ? null : Bool(message, "keep"));
                break;
            case "open-log": AppLog.OpenLog(); break;
            case "open-log-folder": AppLog.OpenLogFolder(); break;
            case "open-user-folder":
                try
                {
                    RuseFolders.OpenUserFolder();
                }
                catch (Exception ex)
                {
                    SetStatus("Could not open the user folder: " + ex.Message, isError: true);
                }
                break;
            case "copy-diagnostics": CopyDiagnostics(); break;
            case "window": HandleWindowCommand(String(message, "action")); break;
        }
    }

    private void HandleWindowCommand(string action)
    {
        switch (action)
        {
            case "minimize": _window.WindowState = WindowState.Minimized; break;
            case "maximize":
                _window.WindowState = _window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                break;
            case "close": _window.Close(); break;
            case "drag":
                WindowDrag.Begin(_window);
                break;
        }
    }

    private T? Payload<T>(BridgeMessage message) =>
        message.Payload.ValueKind == JsonValueKind.Undefined
            ? default
            : message.Payload.Deserialize<T>(_json);

    private static string String(BridgeMessage message, string name, string fallback = "")
    {
        return message.Payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static double Number(BridgeMessage message, string name, double fallback = 0)
    {
        return message.Payload.TryGetProperty(name, out var value) && value.TryGetDouble(out double number)
            ? number
            : fallback;
    }

    private static bool Bool(BridgeMessage message, string name)
    {
        return message.Payload.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static bool Bool(BridgeMessage message, string name, bool fallback)
    {
        if (!message.Payload.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.True;
    }

    private static string[] Strings(BridgeMessage message, string name)
    {
        if (!message.Payload.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
    }

    private void Post(string type, object payload)
    {
        if (_disposed || _core == null)
        {
            return;
        }

        try
        {
            _core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type, payload }, _json));
            _postFailureLogged = false;
        }
        catch (Exception ex)
        {
            // A single bad telemetry frame must never take down the whole app.
            if (!_postFailureLogged)
            {
                _postFailureLogged = true;
                AppLog.Error($"Could not send the UI message '{type}'.", ex);
            }
        }
    }

    private void PublishAll()
    {
        Post("catalog", _catalog);
        PublishGraph();
        PublishHistory();
        PublishSettings();
        PublishTelemetry(force: true);
        Post("status", new StatusDto { Text = _statusText });
    }

    private void PublishGraph()
    {
        var warnings = BuildGraphWarnings();
        var nodes = _nodes.Values.Select(CreateNodeDto).ToList();
        foreach (var node in nodes)
        {
            var warning = warnings
                .Where(item => item.SourceId == node.Id || item.OutputId == node.Id)
                .OrderBy(item => item.Severity == "warning" ? 0 : 1)
                .FirstOrDefault();
            if (warning == null)
            {
                continue;
            }

            node.DuplicateRisk = true;
            node.DuplicateWarning = warning.Message;
            node.WarningSeverity = warning.Severity;
        }

        var dto = new GraphDto
        {
            Version = ++_graphVersion,
            Running = _running,
            Nodes = nodes,
            Links = _links.Select(link => new LinkDto
            {
                Id = link.Id,
                From = link.From,
                To = link.To,
                Kind = link.Kind,
                Gain = link.Gain,
                Pan = link.Pan,
                DelayMs = link.DelayMs,
                Enabled = link.Enabled,
                Solo = link.Solo,
                BusB = link.BusB,
                Inverted = link.Inverted,
                Native = link.Native,
                NativeFallback = IsNativeFallback(link),
                NativeRate = link.Native && _nodes.TryGetValue(link.From, out var nativeFrom)
                    ? nativeFrom.Source?.SampleRate ?? 0
                    : 0,
            }).ToList(),
            Frames = _frames.Select(frame => new FrameDto
            {
                Id = frame.Id,
                Name = frame.Name,
                X = frame.X,
                Y = frame.Y,
                Width = frame.Width,
                Height = frame.Height,
            }).ToList(),
            Warnings = warnings,
        };
        Post("graph", dto);
        PublishControlGraphIfDue();
    }

    /// <summary>
    /// Sends only the mutable state of one node. Use this for gain, mute, solo,
    /// bypass and color instead of rebuilding and republishing the whole graph.
    /// </summary>
    private void PublishNodeState(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry))
        {
            return;
        }

        var state = new NodeStateDto
        {
            Id = entry.Id,
            Color = entry.Color ?? "",
            Accent = string.IsNullOrWhiteSpace(entry.Color) ? entry.Accent : entry.Color,
        };

        if (entry.Source != null)
        {
            state.Volume = entry.Source.Gain;
            state.Muted = entry.Source.Muted;
            state.Solo = entry.Source.Solo;
        }
        else if (entry.Output != null)
        {
            state.Volume = entry.Output.Volume;
            state.Muted = entry.Output.Muted;
            state.Solo = entry.Output.Solo;
        }
        else if (entry.Modifier != null)
        {
            state.Enabled = entry.Modifier.Enabled;
            state.Send = entry.Modifier.Send;
            state.Parallel = entry.Modifier.Parallel;
        }

        string? groupKey = entry.Source?.GroupKey ?? entry.Output?.GroupKey;
        if (!string.IsNullOrWhiteSpace(groupKey))
        {
            state.GroupGain = MixerGroupBus.Gain(groupKey);
            state.GroupMuted = MixerGroupBus.Muted(groupKey);
            state.GroupSolo = MixerGroupBus.Solo(groupKey);
        }

        Post("node-state", state);
        _controlServer?.PublishNodeState(state);
    }

    /// <summary>Sends one cable's gain and enabled state without a graph rebuild.</summary>
    private void PublishLinkState(string id)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null)
        {
            return;
        }

        var state = new LinkStateDto
        {
            Id = link.Id,
            Gain = link.Gain,
            Pan = link.Pan,
            DelayMs = link.DelayMs,
            Enabled = link.Enabled,
            Solo = link.Solo,
            BusB = link.BusB,
            Inverted = link.Inverted,
            Native = link.Native,
            NativeFallback = IsNativeFallback(link),
            NativeRate = link.Native && _nodes.TryGetValue(link.From, out var nativeFrom)
                ? nativeFrom.Source?.SampleRate ?? 0
                : 0,
        };
        Post("link-state", state);
        _controlServer?.PublishLinkState(state);
    }

    /// <summary>Flags routes that copy audio already playing on the output.</summary>
    internal List<GraphWarningDto> BuildGraphWarnings()
    {
        var warnings = new List<GraphWarningDto>();
        if (App.DemoMode)
        {
            return warnings;
        }

        var sources = _nodes.Values
            .Where(entry => entry.Source != null)
            .ToDictionary(entry => entry.Source!, entry => entry);
        var outputs = _nodes.Values
            .Where(entry => entry.Output != null)
            .ToDictionary(entry => entry.Output!, entry => entry);

        foreach (var outputNode in _graph.Outputs)
        {
            if (outputNode.IsFileRecorder ||
                !outputs.TryGetValue(outputNode, out var outputEntry))
            {
                continue;
            }

            foreach (var route in outputNode.Inputs)
            {
                if (route.Source == null ||
                    !sources.TryGetValue(route.Source, out var sourceEntry) ||
                    string.IsNullOrEmpty(sourceEntry.SourceEndpointId))
                {
                    continue;
                }

                bool sameDevice = string.Equals(
                    sourceEntry.SourceEndpointId, outputNode.DeviceId, StringComparison.OrdinalIgnoreCase);
                if (!sameDevice)
                {
                    continue;
                }

                string sourceDeviceName = sourceEntry.SourceEndpointName ?? "its current output";
                warnings.Add(new GraphWarningDto
                {
                    Severity = "warning",
                    SourceId = sourceEntry.Id,
                    OutputId = outputEntry.Id,
                    DeviceName = sourceDeviceName,
                    Message = $"{sourceEntry.Title} is already playing on {sourceDeviceName}. " +
                              "Ruse is sending a second copy to the same device, so you will hear it twice.",
                });
            }
        }

        return warnings;
    }

    /// <summary>Shows the prerelease tag in Settings and the welcome splash.</summary>
    private static string AppVersionText
    {
        get
        {
            var assembly = typeof(WebShellController).Assembly;
            string? text = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(text))
            {
                int metadata = text.IndexOf('+');
                return metadata >= 0 ? text[..metadata] : text;
            }

            return assembly.GetName().Version?.ToString(3) ?? "dev";
        }
    }

    private void PublishSettings()
    {
        int installedCount = 0;
        long installedBytes = 0;
        var models = TranscriptionModels.All.Select(model =>
        {
            bool installed = TranscriptionModels.IsInstalled(model.Id);
            long bytes = installed ? TranscriptionModels.GetInstalledSizeBytes(model.Id) : 0;
            if (installed)
            {
                installedCount++;
                installedBytes += bytes;
            }

            return new TranscriberModelDto
            {
                Id = model.Id,
                Language = model.Language,
                Name = model.Name,
                Tier = model.Tier,
                SizeMb = model.SizeMb,
                VramMb = model.VramMb,
                Note = model.Note,
                Installed = installed,
                InstalledSize = installed ? TranscriptionModels.FormatSize(bytes) : "",
            };
        }).ToList();

        int installedTranslationCount = 0;
        long installedTranslationBytes = 0;
        var translationModels = TranslationModels.All.Select(model =>
        {
            bool installed = TranslationModels.IsInstalled(model.Id);
            long bytes = installed ? TranslationModels.GetInstalledSizeBytes(model.Id) : 0;
            if (installed)
            {
                installedTranslationCount++;
                installedTranslationBytes += bytes;
            }

            return new TranslatorModelDto
            {
                Id = model.Id,
                Name = model.Name,
                Tier = model.Tier,
                SizeMb = model.SizeMb,
                VramMb = model.VramMb,
                License = model.License,
                Note = model.Note,
                Installed = installed,
                InstalledSize = installed ? TranslationModels.FormatSize(bytes) : "",
            };
        }).ToList();

        int installedOpusCount = 0;
        long installedOpusBytes = 0;
        var opusModels = OpusTranslationModels.All.Select(model =>
        {
            bool installed = OpusTranslationModels.IsInstalled(model.Id);
            long bytes = installed ? OpusTranslationModels.GetInstalledSizeBytes(model.Id) : 0;
            if (installed)
            {
                installedOpusCount++;
                installedOpusBytes += bytes;
            }

            return new OpusModelDto
            {
                Id = model.Id,
                Name = model.Name,
                Source = model.Source,
                Target = model.Target,
                SizeMb = model.SizeMb,
                License = model.License,
                Note = model.Prefix == null ? null : model.Prefix.Trim(),
                Installed = installed,
                InstalledSize = installed ? TranslationModels.FormatSize(bytes) : "",
            };
        }).ToList();

        int installedTtsCount = 0;
        long installedTtsBytes = 0;
        var ttsModels = TtsModels.All.Select(model =>
        {
            bool installed = TtsModels.IsInstalled(model.Id);
            long bytes = installed ? TtsModels.GetInstalledSizeBytes(model.Id) : 0;
            if (installed)
            {
                installedTtsCount++;
                installedTtsBytes += bytes;
            }

            return new TtsModelDto
            {
                Id = model.Id,
                Name = model.Name,
                Languages = model.Languages,
                SizeMb = (int)Math.Round(model.SizeMb),
                License = model.License,
                Note = model.Note,
                Installed = installed,
                InstalledSize = installed ? TtsModels.FormatSize(bytes / 1024.0 / 1024.0) : "",
            };
        }).ToList();
        Post("settings", new SettingsDto
        {
            AutoLoadSession = _settings.AutoLoadSession,
            FollowPlayingApps = _settings.FollowPlayingApps,
            RestoreAppRoutes = _settings.RestoreAppRoutes,
            TimelineBounceOutputId = _settings.TimelineBounceOutputId,
            TimelineBounceTailSeconds = _settings.TimelineBounceTailSeconds,
            FollowOutputId = _settings.FollowOutputId,
            UiLanguage = _settings.UiLanguage,
            Theme = _settings.Theme,
            UiScale = _settings.UiScale,
            MotionMode = _settings.MotionMode,
            StartRoutingOnLaunch = _settings.StartRoutingOnLaunch,
            HighlightMutedNodes = _settings.HighlightMutedNodes,
            SignalGlow = _settings.SignalGlow,
            SnapToGrid = _settings.SnapToGrid,
            HighlightCableOnHover = _settings.HighlightCableOnHover,
            CableAnimation = _settings.CableAnimation,
            CablePhysics = _settings.CablePhysics,
            ConnectorAnimation = _settings.ConnectorAnimation,
            MinimapMode = _settings.MinimapMode,
            StartWithWindows = StartupRegistration.IsEnabled(),
            ShowWelcomeScreen = _settings.ShowWelcomeScreen,
            MinimizeToTray = _settings.MinimizeToTray,
            StartMinimized = _settings.StartMinimized,
            MinimizeToTrayOnMinimize = _settings.MinimizeToTrayOnMinimize,
            ShowDiagnostics = _settings.ShowDiagnostics,
            ControlApiEnabled = _settings.ControlApiEnabled,
            ControlApiPort = _settings.ControlApiPort,
            ControlApiUrl = _controlServer?.Url ?? "",
            ControlApiToken = _controlServer?.Token ?? "",
            UpdateAutoCheck = _settings.UpdateAutoCheck,
            UpdateCheckIntervalDays = _settings.UpdateCheckIntervalDays is 1 or 7 or 30
                ? _settings.UpdateCheckIntervalDays
                : 1,
            AggressiveOutputCushion = _settings.AggressiveOutputCushion,
            ClipGuard = _settings.ClipGuard,
            LowLatencyWasapi = _settings.LowLatencyWasapi,
            AudioAutoMode = _settings.AudioAutoMode,
            LowLatencyMode = _settings.LowLatencyMode,
            AudioBlockSizeMs = _settings.AudioBlockSizeMs,
            OutputBufferMode = _settings.OutputBufferMode,
            OutputBufferManualMs = _settings.OutputBufferManualMs,
            EngineCushionMode = _settings.EngineCushionMode,
            EngineCushionManualMs = _settings.EngineCushionManualMs,
            ElasticRing = _settings.ElasticRing,
            UseGpu = _settings.UseGpu,
            IsolateVst3 = _settings.IsolateVst3,
            MidiEnabled = _settings.MidiEnabled,
            MidiInputDevice = _settings.MidiInputDevice,
            MidiOpen = _midi.IsOpen,
            MidiError = _midi.LastError,
            MidiInputs = _midi.Devices.Select(device => new MidiInputDto { Name = device.Name }).ToList(),
            GpuAvailable = ModelRuntime.GpuAvailable,
            GpuDeviceName = ModelRuntime.GpuDeviceName,
            GpuStatusError = ModelRuntime.GpuStatusError,
            GpuFallbackReason = ModelRuntime.GpuFallbackReason,
            TranscriberModel = _settings.TranscriberModel,
            TranscriberModels = models,
            TranscriberDownload = _modelDownload == null
                ? null
                : new TranscriberDownloadDto
                {
                    Id = _modelDownload.ModelId,
                    Phase = _modelDownload.Phase,
                    Percent = _modelDownload.Percent,
                    Message = _modelDownload.Message,
                },
            TranslatorModel = _settings.TranslatorModel,
            TranslatorTarget = _settings.TranslatorTarget,
            TranslationProvider = _settings.TranslationProvider,
            TranslationGlossary = _settings.TranslationGlossary,
            TranslationMemoryEnabled = _settings.TranslationMemoryEnabled,
            TranslatorModels = translationModels,
            TtsModel = _settings.TtsModel,
            TtsSpeed = _settings.TtsSpeed,
            TtsModels = ttsModels,
            TtsDownload = _ttsDownload == null
                ? null
                : new TtsDownloadDto
                {
                    Id = _ttsDownload.Id,
                    Phase = _ttsDownload.Phase,
                    Percent = (int)Math.Round(_ttsDownload.Progress * 100),
                    Message = _ttsDownload.Error ?? "",
                },
            StreamingAsrEnabled = _settings.StreamingAsrEnabled,
            VoskInstalled = VoskModels.IsInstalled,
            VoskSize = VoskModels.IsInstalled ? TranscriptionModels.FormatSize(VoskModels.GetInstalledSizeBytes()) : "",
            VoskDownload = _voskDownload == null
                ? null
                : new TtsDownloadDto
                {
                    Id = _voskDownload.Id,
                    Phase = _voskDownload.Phase,
                    Percent = (int)Math.Round(_voskDownload.Progress * 100),
                    Message = _voskDownload.Error ?? "",
                },
            InstalledTtsModelsCount = installedTtsCount,
            InstalledTtsModelsSize = TtsModels.FormatSize(installedTtsBytes / 1024.0 / 1024.0),
            OpusModels = opusModels,
            TranslatorDownload = _translationDownload == null
                ? null
                : new TranslatorDownloadDto
                {
                    Id = _translationDownload.ModelId,
                    Phase = _translationDownload.Phase,
                    Percent = _translationDownload.Percent,
                    Message = _translationDownload.Message,
                },
            InstalledModelsCount = installedCount,
            InstalledModelsSize = TranscriptionModels.FormatSize(installedBytes),
            InstalledTranslatorModelsCount = installedTranslationCount,
            InstalledTranslatorModelsSize = TranslationModels.FormatSize(installedTranslationBytes),
            InstalledOpusModelsCount = installedOpusCount,
            InstalledOpusModelsSize = TranslationModels.FormatSize(installedOpusBytes),
            AppVersion = AppVersionText,
            DotNetVersion = Environment.Version.ToString(),
            WebView2Version = string.IsNullOrEmpty(_webView2Version) ? "not initialized" : _webView2Version,
            RenderTier = System.Windows.Media.RenderCapability.Tier >> 16,
            DataFolder = AppSettings.Directory,
            ModelsFolder = TranscriptionModels.Root,
            UserFolder = RuseFolders.UserRoot,
        });
    }

    private void PublishTelemetry(bool force = false)
    {
        bool active = _graph.Outputs.Any(output => output.IsRunning) ||
                      _graph.Sources.Any(source => source.IsRunning) ||
                      _nodes.Values.Any(entry => entry.Output?.IsRecording == true);
        long now = Environment.TickCount64;
        if (!force && !active && now - _lastTelemetryPublish < 1000)
        {
            return;
        }

        // The WebView is invisible while minimized or hidden in the tray; keep
        // the engine alive but stop serializing full telemetry at 10 Hz.
        bool hidden = !_window.IsVisible || _window.WindowState == WindowState.Minimized;
        if (!force && hidden && now - _lastTelemetryPublish < 1000)
        {
            return;
        }

        _lastTelemetryPublish = now;
        PollTtsSources();
        long telemetryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        TelemetryDto telemetry = _telemetry;
        telemetry.Running = _running;
        telemetry.Clipping = false;
        telemetry.MasterGain = Finite(OutputNode.MasterGain);
        telemetry.MasterMuted = OutputNode.MasterMuted;
        telemetry.MicMuted = _micMuted;
        telemetry.Diagnostics = null;

        var sourceLevels = _telemetrySourceLevels;
        var sourceClipping = _telemetrySourceClipping;
        var outputClipping = _telemetryOutputClipping;
        var modifierLevels = _telemetryModifierLevels;
        var visiting = _telemetryVisiting;
        var clippingUpstream = _telemetryClippingUpstream;
        var clippingDownstream = _telemetryClippingDownstream;
        var seen = _telemetrySeenIds;
        sourceLevels.Clear();
        sourceClipping.Clear();
        outputClipping.Clear();
        modifierLevels.Clear();
        seen.Clear();
        LinkIndex? linkIndex = null;

        foreach (var entry in _nodes.Values)
        {
            if (entry.Source is { } source)
            {
                sourceLevels[entry.Id] = Meter(source.MeterLevel);
                sourceClipping[entry.Id] = source.Clipping;
            }
            else if (entry.Output is { } output)
            {
                outputClipping[entry.Id] = output.Clipping;
            }
        }

        foreach (var entry in _nodes.Values)
        {
            if (!telemetry.Meters.TryGetValue(entry.Id, out var meter))
            {
                meter = new NodeTelemetryDto();
                telemetry.Meters[entry.Id] = meter;
            }
            else
            {
                meter.Reset();
            }

            seen.Add(entry.Id);
            if (entry.Source != null)
            {
                meter.Level = entry.Source.Muted ? 0 : Meter(entry.Source.MeterLevel);
                meter.LevelLeft = entry.Source.Muted ? 0 : Meter(entry.Source.MeterLevelLeft);
                meter.LevelRight = entry.Source.Muted ? 0 : Meter(entry.Source.MeterLevelRight);
                meter.MeterDbLeft = entry.Source.Muted ? -60f : entry.Source.MeterDbLeft;
                meter.MeterDbRight = entry.Source.Muted ? -60f : entry.Source.MeterDbRight;
                meter.Clipping = !entry.Source.Muted && entry.Source.Clipping;
                meter.FilePlaying = entry.Source.Kind == SourceKind.File &&
                                    entry.Source.IsRunning &&
                                    !entry.Source.FilePaused;
                meter.FilePaused = entry.Source.Kind == SourceKind.File && entry.Source.FilePaused;
                meter.FilePositionSeconds = entry.Source.Kind == SourceKind.File ? entry.Source.FilePositionSeconds : 0;
                meter.FileDurationSeconds = entry.Source.Kind == SourceKind.File ? entry.Source.FileDurationSeconds : 0;
                if (_settings.ShowDiagnostics)
                {
                    meter.InputLatencyMs = Finite(entry.Source.CaptureLatencyMs + entry.Source.BufferedMs);
                }
            }
            else if (entry.Output != null)
            {
                meter.Level = Meter(entry.Output.MeterLevel);
                meter.LevelLeft = Meter(entry.Output.MeterLevelLeft);
                meter.LevelRight = Meter(entry.Output.MeterLevelRight);
                meter.MeterDbLeft = entry.Output.MeterDbLeft;
                meter.MeterDbRight = entry.Output.MeterDbRight;
                meter.Clipping = entry.Output.Clipping;
                meter.ClipGuardActive = entry.Output.ClipGuardActive;
                if (entry.Output.IsFileRecorder)
                {
                    meter.RecordingStatus = RecorderStatus(entry.Output);
                    meter.RecordingSeconds = Finite(entry.Output.RecordedSeconds);
                    meter.RecordingError = entry.Output.RecordingError;
                }
                if (_settings.ShowDiagnostics)
                {
                    meter.OutputLatencyMs = Finite(entry.Output.EstimatedLatencyMs);
                }
            }
            else
            {
                linkIndex ??= GetLinkIndex();
                visiting.Clear();
                clippingUpstream.Clear();
                clippingDownstream.Clear();
                meter.Level = Meter(ModifierLevel(
                    entry.Id, sourceLevels, modifierLevels, visiting, linkIndex));
                meter.LevelLeft = meter.Level;
                meter.LevelRight = meter.Level;
                meter.Clipping = ModifierClipping(entry.Id, sourceClipping, outputClipping,
                    clippingUpstream, clippingDownstream, linkIndex);
                if (entry.Modifier is { IsMixer: true } mixerMeter)
                {
                    float dbLeft = Finite(mixerMeter.MixerMeterDbLeft);
                    float dbRight = Finite(mixerMeter.MixerMeterDbRight);
                    meter.DbLeft = dbLeft;
                    meter.DbRight = dbRight;
                    meter.Db = Math.Max(dbLeft, dbRight);
                    meter.PeakDbLeft = Finite(mixerMeter.MixerMeterPeakDbLeft);
                    meter.PeakDbRight = Finite(mixerMeter.MixerMeterPeakDbRight);
                    meter.PeakDb = Math.Max(meter.PeakDbLeft.Value, meter.PeakDbRight.Value);
                    meter.LevelLeft = Math.Clamp(MathF.Pow(10f, dbLeft / 20f), 0f, 1f);
                    meter.LevelRight = Math.Clamp(MathF.Pow(10f, dbRight / 20f), 0f, 1f);
                    meter.Level = Math.Max(meter.LevelLeft, meter.LevelRight);
                    meter.Clipping = mixerMeter.MixerMeterClipping;
                }

                if (entry.Modifier is { } modifier)
                {
                    DbMeterProcessor? dbMeter = null;
                    SpectrumProcessor? spectrum = null;
                    SpectrogramProcessor? spectrogram = null;
                    WaveformProcessor? waveform = null;
                    LoudnessProcessor? loudness = null;
                    NoteDetectorProcessor? noteDetector = null;
                    TranslatorProcessor? translator = null;
                    TranscriptionProcessor? transcriber = null;

                    var processors = modifier.Processors;
                    for (int index = 0; index < processors.Count; index++)
                    {
                        var processor = processors[index];
                        dbMeter ??= processor as DbMeterProcessor;
                        spectrum ??= processor as SpectrumProcessor;
                        spectrogram ??= processor as SpectrogramProcessor;
                        waveform ??= processor as WaveformProcessor;
                        loudness ??= processor as LoudnessProcessor;
                        noteDetector ??= processor as NoteDetectorProcessor;
                        translator ??= processor as TranslatorProcessor;
                        transcriber ??= processor as TranscriptionProcessor;
                    }

                    if (dbMeter != null)
                    {
                        meter.DbMeterVertical = dbMeter.IsVertical;
                    }

                    if (modifier.Enabled && spectrum != null)
                    {
                        meter.Spectrum = SpectrumProcessor.GetBands(spectrum.TapId);
                        meter.SpectrumPeakHz = SpectrumProcessor.GetPeakHz(spectrum.TapId);
                    }

                    if (modifier.Enabled && spectrogram != null)
                    {
                        long cursor = _spectrogramCursors.TryGetValue(entry.Id, out var value) ? value : -1;
                        var update = SpectrogramProcessor.GetUpdate(spectrogram.TapId, cursor);
                        if (update is { } next)
                        {
                            meter.Spectrogram = next.Data;
                            meter.SpectrogramNewColumns = next.Count;
                            meter.SpectrogramTotalColumns = next.Total;
                            meter.SpectrogramFull = next.Full;
                            _spectrogramCursors[entry.Id] = next.Total;
                        }

                        meter.SpectrogramPeakHz = SpectrogramProcessor.GetPeakHz(spectrogram.TapId);
                        meter.SpectrogramColumns = SpectrogramProcessor.Columns;
                        meter.SpectrogramWindowSeconds = SpectrogramProcessor.WindowSeconds;
                    }

                    if (modifier.Enabled && waveform != null &&
                        WaveformProcessor.GetReading(waveform.TapId, waveform.Trigger) is { } reading)
                    {
                        meter.Waveform = reading.Samples;
                        meter.WaveformPeak = reading.Peak;
                    }

                    if (modifier.Enabled && dbMeter != null)
                    {
                        meter.Db = DbMeterProcessor.GetDb(dbMeter.TapId);
                        meter.PeakDb = DbMeterProcessor.GetPeakDb(dbMeter.TapId);
                        meter.DbLeft = DbMeterProcessor.GetDbLeft(dbMeter.TapId);
                        meter.DbRight = DbMeterProcessor.GetDbRight(dbMeter.TapId);
                        meter.PeakDbLeft = DbMeterProcessor.GetPeakDbLeft(dbMeter.TapId);
                        meter.PeakDbRight = DbMeterProcessor.GetPeakDbRight(dbMeter.TapId);
                    }

                    if (modifier.Enabled && loudness != null)
                    {
                        meter.LoudnessIntegrated = LoudnessProcessor.GetIntegratedLufs(loudness.TapId);
                        meter.LoudnessShortTerm = LoudnessProcessor.GetShortTermLufs(loudness.TapId);
                        meter.LoudnessRange = LoudnessProcessor.GetLoudnessRange(loudness.TapId);
                        meter.LoudnessTruePeak = LoudnessProcessor.GetTruePeakDb(loudness.TapId);
                    }

                    if (modifier.Enabled && noteDetector != null &&
                        NoteDetectorProcessor.TryGetReading(noteDetector.TapId, out var note))
                    {
                        meter.NoteName = note.Name;
                        meter.NoteMidi = note.Midi;
                        meter.NoteCents = Finite(note.Cents);
                        meter.NoteHz = Finite(note.Hz);
                        meter.NoteConfidence = Finite(note.Confidence);
                        meter.NoteActive = true;
                    }

                    if (modifier.Enabled && translator != null)
                    {
                        meter.TranscriptionLevel = Meter(TranscriptionProcessor.GetLevel(translator.TapId));
                        long lastAudio = TranscriptionProcessor.GetLastAudioTick(translator.TapId);
                        meter.TranscriptionHasAudio = lastAudio > 0 && now - lastAudio < 2500;

                        if (string.IsNullOrEmpty(entry.TranscriberModel))
                        {
                            meter.TranscriptionStatus = "No speech model installed";
                        }
                        else
                        {
                            var snapshot = TranslationRegistry.Snapshot(translator.TapId);
                            if (!_telemetryText.TryGetValue(entry.Id, out var textCache))
                            {
                                textCache = new TelemetryTextCache();
                                _telemetryText[entry.Id] = textCache;
                            }

                            meter.TranscriptionStatus = snapshot?.Status ?? "Starting";
                            if (snapshot != null)
                            {
                                if (!string.Equals(textCache.Transcript, snapshot.SourceText, StringComparison.Ordinal))
                                {
                                    textCache.Transcript = snapshot.SourceText;
                                    meter.Transcript = snapshot.SourceText;
                                }
                                if (!string.Equals(textCache.PartialTranscript, snapshot.SourcePartial, StringComparison.Ordinal))
                                {
                                    textCache.PartialTranscript = snapshot.SourcePartial;
                                    meter.PartialTranscript = snapshot.SourcePartial;
                                }
                                if (!string.Equals(textCache.TranslatedTranscript, snapshot.TranslatedText, StringComparison.Ordinal))
                                {
                                    textCache.TranslatedTranscript = snapshot.TranslatedText;
                                    meter.TranslatedTranscript = snapshot.TranslatedText;
                                }
                                if (!string.Equals(textCache.TranslatedPartial, snapshot.TranslatedPartial, StringComparison.Ordinal))
                                {
                                    textCache.TranslatedPartial = snapshot.TranslatedPartial;
                                    meter.TranslatedPartial = snapshot.TranslatedPartial;
                                }
                            }
                            else
                            {
                                if (textCache.Transcript != null) { textCache.Transcript = ""; meter.Transcript = ""; }
                                if (textCache.PartialTranscript != null) { textCache.PartialTranscript = ""; meter.PartialTranscript = ""; }
                                if (textCache.TranslatedTranscript != null) { textCache.TranslatedTranscript = ""; meter.TranslatedTranscript = ""; }
                                if (textCache.TranslatedPartial != null) { textCache.TranslatedPartial = ""; meter.TranslatedPartial = ""; }
                            }
                            meter.DetectedLanguage = snapshot?.DetectedLanguage;
                            meter.TranscriptionError = snapshot?.Error;
                            meter.TranscriptionElapsed = snapshot?.ElapsedSeconds;
                            meter.UsesGpu = snapshot?.GpuActive;
                        }
                    }
                    else if (modifier.Enabled && transcriber != null)
                    {
                        meter.TranscriptionLevel = Meter(TranscriptionProcessor.GetLevel(transcriber.TapId));
                        long lastAudio = TranscriptionProcessor.GetLastAudioTick(transcriber.TapId);
                        meter.TranscriptionHasAudio = lastAudio > 0 && now - lastAudio < 2500;

                        if (string.IsNullOrEmpty(entry.TranscriberModel))
                        {
                            meter.TranscriptionStatus = "No speech model installed";
                        }
                        else
                        {
                            var snapshot = TranscriptionRegistry.Snapshot(transcriber.TapId);
                            if (!_telemetryText.TryGetValue(entry.Id, out var textCache))
                            {
                                textCache = new TelemetryTextCache();
                                _telemetryText[entry.Id] = textCache;
                            }

                            meter.TranscriptionStatus = snapshot?.Status ?? "Starting";
                            if (snapshot != null)
                            {
                                if (!string.Equals(textCache.Transcript, snapshot.Transcript, StringComparison.Ordinal))
                                {
                                    textCache.Transcript = snapshot.Transcript;
                                    meter.Transcript = snapshot.Transcript;
                                }
                                if (!string.Equals(textCache.PartialTranscript, snapshot.Partial, StringComparison.Ordinal))
                                {
                                    textCache.PartialTranscript = snapshot.Partial;
                                    meter.PartialTranscript = snapshot.Partial;
                                }
                            }
                            else
                            {
                                if (textCache.Transcript != null) { textCache.Transcript = ""; meter.Transcript = ""; }
                                if (textCache.PartialTranscript != null) { textCache.PartialTranscript = ""; meter.PartialTranscript = ""; }
                            }
                            meter.TranscriptionError = snapshot?.Error;
                            meter.TranscriptionElapsed = snapshot?.ElapsedSeconds;
                            meter.UsesGpu = snapshot?.GpuActive;
                            meter.DetectedLanguage = snapshot?.DetectedLanguage;
                        }
                    }
                }
            }

            if (meter.Clipping) _nodeClipHoldUntil[entry.Id] = now + ClipHoldMs;
            meter.Clipping = meter.Clipping ||
                             (_nodeClipHoldUntil.TryGetValue(entry.Id, out long until) && now < until);
            telemetry.Clipping |= meter.Clipping;
            telemetry.Meters[entry.Id] = meter;
        }

        var stale = _telemetryStaleIds;
        stale.Clear();
        foreach (var id in telemetry.Meters.Keys)
        {
            if (!seen.Contains(id))
            {
                stale.Add(id);
            }
        }

        foreach (var id in stale)
        {
            telemetry.Meters.Remove(id);
            _telemetryText.Remove(id);
        }

        _running = telemetry.Running;
        if (_settings.ShowDiagnostics)
        {
            telemetry.Diagnostics = BuildDiagnostics(now);
        }

        if (!string.Equals(_lastGpuFallbackReason, ModelRuntime.GpuFallbackReason, StringComparison.Ordinal))
        {
            _lastGpuFallbackReason = ModelRuntime.GpuFallbackReason;
            PublishSettings();
        }

        Post("telemetry", telemetry);
        _lastTelemetryMs = (System.Diagnostics.Stopwatch.GetTimestamp() - telemetryStarted)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static float Finite(float value, float fallback = 0f) =>
        float.IsFinite(value) ? value : fallback;

    private static double Finite(double value, double fallback = 0) =>
        double.IsFinite(value) ? value : fallback;

    private static float Meter(float value) => Math.Clamp(Finite(value), 0f, 1f);

    /// <summary>Modifier activity is the hottest upstream source.</summary>
    private float ModifierLevel(
        string id,
        Dictionary<string, float> sourceLevels,
        Dictionary<string, float> cache,
        HashSet<string> visiting,
        LinkIndex linkIndex)
    {
        if (cache.TryGetValue(id, out float cached)) return cached;
        if (!visiting.Add(id)) return 0;

        float level = 0;
        if (linkIndex.Incoming.TryGetValue(id, out var incoming))
        {
            foreach (var link in incoming)
            {
                if (link.Kind == "source-modifier" &&
                    sourceLevels.TryGetValue(link.From, out float sourceLevel))
                {
                    level = Math.Max(level, sourceLevel);
                }
                else if (link.Kind == "modifier-modifier")
                {
                    level = Math.Max(level,
                        ModifierLevel(link.From, sourceLevels, cache, visiting, linkIndex));
                }
            }
        }

        visiting.Remove(id);
        cache[id] = level;
        return level;
    }

    private bool ModifierClipping(
        string id,
        Dictionary<string, bool> sourceClipping,
        Dictionary<string, bool> outputClipping,
        HashSet<string> upstream,
        HashSet<string> downstream,
        LinkIndex linkIndex)
    {
        if (ReachesClippingOutput(id, outputClipping, downstream, linkIndex)) return true;
        if (!upstream.Add(id)) return false;

        if (linkIndex.Incoming.TryGetValue(id, out var incoming))
        {
            foreach (var link in incoming)
            {
                if (link.Kind == "source-modifier" &&
                    sourceClipping.TryGetValue(link.From, out bool clipped) && clipped)
                {
                    return true;
                }

                if (link.Kind == "modifier-modifier" &&
                    ModifierClipping(link.From, sourceClipping, outputClipping,
                        upstream, downstream, linkIndex))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool ReachesClippingOutput(
        string id,
        Dictionary<string, bool> outputClipping,
        HashSet<string> visited,
        LinkIndex linkIndex)
    {
        if (!visited.Add(id)) return false;

        if (linkIndex.Outgoing.TryGetValue(id, out var outgoing))
        {
            foreach (var link in outgoing)
            {
                if (link.Kind == "modifier-output" &&
                    outputClipping.TryGetValue(link.To, out bool clipped) && clipped)
                {
                    return true;
                }

                if (link.Kind == "modifier-modifier" &&
                    ReachesClippingOutput(link.To, outputClipping, visited, linkIndex))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private LinkIndex GetLinkIndex() => _cachedLinkIndex ??= BuildLinkIndex();

    private void InvalidateLinkIndex() => _cachedLinkIndex = null;

    private LinkIndex BuildLinkIndex()
    {
        var index = new LinkIndex();
        foreach (var link in _links)
        {
            if (!index.Incoming.TryGetValue(link.To, out var incoming))
            {
                incoming = new List<LinkEntry>();
                index.Incoming[link.To] = incoming;
            }

            incoming.Add(link);

            if (!index.Outgoing.TryGetValue(link.From, out var outgoing))
            {
                outgoing = new List<LinkEntry>();
                index.Outgoing[link.From] = outgoing;
            }

            outgoing.Add(link);
        }

        return index;
    }

    private sealed class LinkIndex
    {
        public Dictionary<string, List<LinkEntry>> Incoming { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<LinkEntry>> Outgoing { get; } = new(StringComparer.Ordinal);
    }

    private static string RecorderStatus(OutputNode output)
    {
        if (!string.IsNullOrEmpty(output.RecordingError)) return "ERR";
        if (output.IsRecording) return "REC";
        if (output.RecorderMode == RecorderMode.Off) return "OFF";
        if (!output.AutoSave && !output.HasRecordingPath) return "NO FILE";
        if (output.RecorderMode == RecorderMode.Armed) return "ARMED";
        if (output.RecordedSeconds > 0) return "SAVED";
        return "ON";
    }

    private void SetStatus(string text, bool isError = false)
    {
        _statusText = text;
        Post("status", new StatusDto { Text = text, IsError = isError });
    }

    private void ShowError(string text, Exception? ex = null,
        string? actionLabel = null, string? action = null)
    {
        if (ex != null)
        {
            AppLog.Error(text, ex);
        }

        SetStatus(text, isError: true);
        Post("alert", new StatusDto
        {
            Text = text,
            IsError = true,
            ActionLabel = actionLabel,
            Action = action,
        });
    }

    private void ShowToast(string text, bool isError = false) =>
        Post("toast", new StatusDto { Text = text, IsError = isError });

    /// <summary>Matches the 6 px native resize margin to the web workspace theme.</summary>
    private void ApplyWindowTheme()
    {
        string theme = _settings.Theme;
        Color color = theme switch
        {
            "light" => Color.FromRgb(0xEE, 0xF1, 0xF6),
            "blueprint" => Color.FromRgb(0x08, 0x3B, 0x78),
            "studio74" => Color.FromRgb(0x1E, 0x1D, 0x1B),
            "receiver" => Color.FromRgb(0x1E, 0x17, 0x12),
            _ => Color.FromRgb(0x17, 0x1E, 0x29),
        };
        _window.Background = new SolidColorBrush(color);
    }

    private static double ClampUiScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(Math.Round(value, 2), 0.75, 1.5) : 1.0;

    private void ApplyUiScale()
    {
        if (_webView == null)
        {
            return;
        }

        try
        {
            _webView.ZoomFactor = ClampUiScale(_settings.UiScale);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not apply the UI scale: " + ex.Message);
        }
    }

    /// <summary>Persists the answer to the first-close tray prompt.</summary>
    internal void SaveTrayPreference(bool minimizeToTray)
    {
        _settings.MinimizeToTray = minimizeToTray;
        _settings.TrayPromptShown = true;
        _settings.Save();
        PublishSettings();
    }

    /// <summary>Asks the web UI for the first-close tray choice. Null cancels.</summary>
    internal bool RequestTrayDecision(Action<bool?> response)
    {
        if (_disposed || _core == null)
        {
            return false;
        }

        _trayDecision = response;
        Post("close-to-tray", new { });
        return true;
    }

    private void ResolveTrayDecision(bool? keep)
    {
        var response = _trayDecision;
        _trayDecision = null;
        response?.Invoke(keep);
    }

    private void RestoreWindowBounds()
    {
        ApplyWindowTheme();
        _window.Width = _settings.Width;
        _window.Height = _settings.Height;
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top) && IsOnScreen(_settings.Left, _settings.Top))
        {
            _window.Left = _settings.Left;
            _window.Top = _settings.Top;
        }

        _window.Loaded += (_, _) => WindowFrameBehavior.ClampToWorkArea(_window);

        // The first launch has no saved placement, so open full screen instead
        // of the small default size. Later launches respect what the user chose.
        if (_settings.IsFirstRun || _settings.Maximized)
        {
            _window.WindowState = WindowState.Maximized;
        }
    }

    private static bool IsOnScreen(double left, double top)
    {
        double minLeft = SystemParameters.VirtualScreenLeft;
        double minTop = SystemParameters.VirtualScreenTop;
        double maxWidth = SystemParameters.VirtualScreenWidth;
        double maxHeight = SystemParameters.VirtualScreenHeight;
        return left >= minLeft - 20 && top >= minTop - 20 &&
               left < minLeft + maxWidth - 100 && top < minTop + maxHeight - 100;
    }

    public void Shutdown()
    {
        if (_shutdownSaved)
        {
            return;
        }

        _shutdownSaved = true;
        try
        {
            _settings.Maximized = _window.WindowState == WindowState.Maximized;
            if (_window.WindowState == WindowState.Normal)
            {
                _settings.Width = _window.Width;
                _settings.Height = _window.Height;
                _settings.Left = _window.Left;
                _settings.Top = _window.Top;
            }

            TranslationMemory.Flush();
            _settings.Save();
            if (!App.LiveDemoMode)
            {
                // An empty graph does not mean the session is disposable: a
                // restore can fail, or the user may have launched with session
                // restore disabled. Only the explicit Clear action deletes it.
                if (_nodes.Count > 0 || _frames.Count > 0)
                {
                    SaveSession();
                }

                PruneGeneratedTimelineFiles(includeHistory: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not save the Web UI session: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updateTimer.Stop();
        _updates.Dispose();
        SpeechActivityHub.SpeechStarted -= OnSpeechStarted;
        SpeechActivityHub.SpeechEnded -= OnSpeechEnded;
        if (_core != null)
        {
            _core.WebMessageReceived -= OnWebMessageReceived;
            _core.ProcessFailed -= OnCoreProcessFailed;
            _core.NavigationStarting -= OnCoreNavigationStarting;
            _core.NewWindowRequested -= OnCoreNewWindowRequested;
        }

        UnregisterDeviceNotifications();
        _telemetryTimer.Stop();
        StopNoiseCalibration();
        _appsTimer.Stop();
        _devicesTimer.Stop();
        foreach (var entry in _nodes.Values)
        {
            entry.TtsGenerationCancel?.Cancel();
        }

        _timelineCountInCancel?.Cancel();
        _timelineBounceCancel?.Cancel();
        _modelDownloadCancel?.Cancel();
        _modelDownloadCancel?.Dispose();
        _translationDownloadCancel?.Cancel();
        _translationDownloadCancel?.Dispose();
        _ttsDownloadCancel?.Cancel();
        _ttsDownloadCancel?.Dispose();
        _voskDownloadCancel?.Cancel();
        _voskDownloadCancel?.Dispose();
        TranscriptionRegistry.StopAll();
        TranslationRegistry.StopAll();
        SpeechModelCache.DropAll();
        TranslationModelCache.DropAll();
        _midi.MessageReceived -= OnMidiMessageReceived;
        _midi.Dispose();
        AbortTimelineRecording();
        SetAudioPriorityBoost(false);
        StopControlServer();
        DisposeInactiveOutputs();
        StopTimelineMetronome();
        StopTimelineAutomationWorker();
        FrozenProxyStore.CleanupTrash();
        _timeline?.Dispose();
        _graph.Dispose();
    }

    internal sealed class NodeEntry
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public required string Flavor { get; init; }
        public required string Title { get; set; }
        public required string Subtitle { get; set; }
        public required string Accent { get; init; }

        /// <summary>Stable, human-readable key for the control API.</summary>
        public string ControlId { get; set; } = "";
        public string? Color { get; set; }
        public string? GroupId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public string? MicHint { get; init; }
        public string? TranscriberModel { get; set; }
        public string? TranslatorTarget { get; set; }
        public string? TranslatorModel { get; set; }
        public string? TtsText { get; set; }
        public string? TtsSourceNodeId { get; set; }
        public string? TtsModel { get; set; }
        public float TtsSpeed { get; set; } = 1f;
        public int TtsVoice { get; set; }
        public string? TtsStatus { get; set; }
        public string? TtsAudioPath { get; set; }
        public int TtsSpokenLength { get; set; }
        public bool TtsGenerating { get; set; }
        public CancellationTokenSource? TtsGenerationCancel { get; set; }
        public string? SourceEndpointId { get; set; }
        public string? SourceEndpointName { get; set; }
        public SourceNode? Source { get; init; }
        public OutputNode? Output { get; init; }
        public ModifierNode? Modifier { get; init; }
        public int Vst3Latency { get; set; } = -1;
        public bool Vst3Ready { get; set; }
    }

    internal sealed class LinkEntry
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public required string From { get; init; }
        public required string To { get; init; }
        public required string Kind { get; init; }
        public float Gain { get; set; } = 1;
        public float Pan { get; set; }
        public float DelayMs { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Solo { get; set; }
        public bool BusB { get; set; }
        public bool Inverted { get; set; }
        public bool Native { get; set; }
    }
}
