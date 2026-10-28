using System.IO;
using System.Text.Json;
using System.Windows;
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

    internal AppSettings UserSettings => _settings;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private AudioGraph _graph = new();
    private readonly Dictionary<string, NodeEntry> _nodes = new();
    private readonly List<LinkEntry> _links = new();
    private readonly Dictionary<string, long> _nodeClipHoldUntil = new();
    private readonly DispatcherTimer _telemetryTimer;
    private readonly DispatcherTimer _appsTimer;
    private CoreWebView2? _core;
    private string _webView2Version = "";
    private CatalogDto _catalog = new();
    private int _graphVersion;
    private bool _running;
    private bool _micMuted;
    private bool _disposed;
    private bool _shutdownSaved;
    private const long ClipHoldMs = 2000;
    private bool _autoStartDone;
    private bool _postFailureLogged;
    private long _lastTelemetryPublish;
    private string _statusText = "Native audio engine ready.";
    private CancellationTokenSource? _modelDownloadCancel;
    private TranscriberModelProgress? _modelDownload;
    private long _lastModelPublish;
    private long _lastModelStatus;

    public WebShellController(WebMainWindow window)
    {
        _window = window;
        _settings = AppSettings.Load();
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _telemetryTimer.Tick += (_, _) => { CheckArmedRecorders(); PublishTelemetry(); };
        _appsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_settings.FollowPlayingApps ? 750 : 4000),
        };
        _appsTimer.Tick += (_, _) => RefreshApps();
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
        _webView2Version = environment.BrowserVersionString;

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
        _core.ProcessFailed += (_, _) => ShowError("The WebView2 UI process failed. Restart Ruse to recover the interface.");
        _core.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith("https://ui.ruse/", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };
        _core.NewWindowRequested += (_, args) => args.Handled = true;

        string webRoot = Path.Combine(AppContext.BaseDirectory, "WebUi");
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
        {
            throw new FileNotFoundException("The Web UI assets are missing. Run 'npm run build' in src/Ruse.App/WebUi.");
        }

        _core.SetVirtualHostNameToFolderMapping(
            "ui.ruse", webRoot, CoreWebView2HostResourceAccessKind.Allow);

        SubgraphLibrary.Load();
        RefreshCatalog();
        if (App.LiveDemoMode)
        {
            BuildLiveGraph(App.LiveDemoProcessId);
        }
        else if (App.DemoMode)
        {
            BuildDemoGraph();
        }
        else if (_settings.AutoLoadSession)
        {
            TryLoadSession();
        }

        RestoreWindowBounds();
        _core.Navigate("https://ui.ruse/index.html");
        _telemetryTimer.Start();
        _appsTimer.Start();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var message = JsonSerializer.Deserialize<BridgeMessage>(e.WebMessageAsJson, _json);
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

    private void HandleMessage(BridgeMessage message)
    {
        switch (message.Type)
        {
            case "ready":
                PublishAll();
                if (!App.DemoMode && !App.UiTestMode && !App.LiveDemoMode && _settings.ShowWelcomeScreen)
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
                break;
            case "refresh": RefreshCatalog(); break;
            case "add": AddFromMessage(message); break;
            case "add-file-player": AddFilePlayer(); break;
            case "file-player-file": ChooseFilePlayerFile(String(message, "id")); break;
            case "file-player-loop": SetFilePlayerLoop(String(message, "id"), Bool(message, "value")); break;
            case "file-player-restart": RestartFilePlayer(String(message, "id")); break;
            case "first-route": CreateFirstRoute(message); break;
            case "onboarding-complete": CompleteOnboarding(); break;
            case "show-welcome": Post("welcome", new { manual = true }); break;
            case "show-splash": Post("splash", new { manual = true }); break;
            case "open-sound-settings": OpenSoundSettings(); break;
            case "move": MoveNode(String(message, "id"), Number(message, "x"), Number(message, "y")); break;
            case "move-many": MoveNodes(message); break;
            case "duplicate": DuplicateNode(String(message, "id")); break;
            case "undo": Undo(); break;
            case "redo": Redo(); break;
            case "remove": RemoveNode(String(message, "id")); break;
            case "connect": ConnectNodes(String(message, "from"), String(message, "to")); break;
            case "disconnect": DisconnectLink(String(message, "id")); break;
            case "disconnect-node": DisconnectNode(String(message, "id")); break;
            case "frames": SetFrames(Payload<FramesPayload>(message)); break;
            case "gain": SetNodeGain(String(message, "id"), (float)Number(message, "value", 1)); break;
            case "mute": SetNodeMute(String(message, "id"), Bool(message, "value")); break;
            case "param": SetParameter(message); break;
            case "effect-enabled": SetEffectEnabled(String(message, "id"), Bool(message, "value")); break;
            case "db-meter-orientation": SetDbMeterOrientation(String(message, "id"), Bool(message, "vertical")); break;
            case "transcriber-model": SetTranscriberModel(String(message, "id"), String(message, "model")); break;
            case "transcriber-download": DownloadTranscriberModel(String(message, "id")); break;
            case "transcriber-download-cancel": CancelTranscriberDownload(); break;
            case "transcriber-delete": DeleteTranscriberModel(String(message, "id")); break;
            case "transcriber-clear": ClearTranscription(String(message, "id")); break;
            case "transcriber-restart": RestartTranscription(String(message, "id")); break;
            case "transcriber-export": ExportTranscription(String(message, "id")); break;
            case "transcriber-open-folder": OpenTranscriberModelsFolder(); break;
            case "rename": RenameNode(String(message, "id"), String(message, "title")); break;
            case "transport":
                if (Bool(message, "running")) StartRouting(); else StopRouting();
                break;
            case "master-gain":
                OutputNode.MasterGain = (float)Number(message, "value", 1);
                PublishTelemetry(force: true);
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
                SetStatus("Graph cleared.");
                PublishGraph();
                break;
            case "save-preset": SavePresetDialog(); break;
            case "load-preset": LoadPresetDialog(); break;
            case "recorder-file": ChooseRecorderFile(String(message, "id"), String(message, "format", "Wav24")); break;
            case "recorder-format": SetRecorderFormat(String(message, "id"), String(message, "format", "Wav24")); break;
            case "recorder-mode": SetRecorderMode(String(message, "id"), String(message, "mode", "Off")); break;
            case "recorder-autosave": SetRecorderAutoSave(String(message, "id"), Bool(message, "value")); break;
            case "recorder-stop": StopRecorder(String(message, "id")); break;
            case "chain-save": SaveChain(message); break;
            case "chain-delete": DeleteChain(String(message, "name")); break;
            case "chain-open": OpenChainForNode(String(message, "id")); break;
            case "settings": ApplySettings(Payload<SettingsDto>(message) ?? new SettingsDto()); break;
            case "open-log": AppLog.OpenLog(); break;
            case "open-log-folder": AppLog.OpenLogFolder(); break;
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
    }

    /// <summary>Flags routes that copy audio already playing on the output.</summary>
    internal List<GraphWarningDto> BuildGraphWarnings()
    {
        var warnings = new List<GraphWarningDto>();
        if (App.DemoMode || App.UiTestMode)
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
            if (outputNode.IsMonitor || outputNode.IsFileRecorder ||
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
                Note = model.Note,
                Installed = installed,
                InstalledSize = installed ? TranscriptionModels.FormatSize(bytes) : "",
            };
        }).ToList();

        Post("settings", new SettingsDto
        {
            AutoLoadSession = _settings.AutoLoadSession,
            FollowPlayingApps = _settings.FollowPlayingApps,
            FollowOutputId = _settings.FollowOutputId,
            UiLanguage = _settings.UiLanguage,
            StartRoutingOnLaunch = _settings.StartRoutingOnLaunch,
            HighlightMutedNodes = _settings.HighlightMutedNodes,
            SignalGlow = _settings.SignalGlow,
            SnapToGrid = _settings.SnapToGrid,
            HighlightCableOnHover = _settings.HighlightCableOnHover,
            CableAnimation = _settings.CableAnimation,
            MinimapMode = _settings.MinimapMode,
            StartWithWindows = StartupRegistration.IsEnabled(),
            ShowWelcomeScreen = _settings.ShowWelcomeScreen,
            MinimizeToTray = _settings.MinimizeToTray,
            ShowDiagnostics = _settings.ShowDiagnostics,
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
            InstalledModelsCount = installedCount,
            InstalledModelsSize = TranscriptionModels.FormatSize(installedBytes),
            AppVersion = typeof(WebShellController).Assembly.GetName().Version?.ToString(3) ?? "dev",
            DotNetVersion = Environment.Version.ToString(),
            WebView2Version = string.IsNullOrEmpty(_webView2Version) ? "not initialized" : _webView2Version,
            RenderTier = System.Windows.Media.RenderCapability.Tier >> 16,
            DataFolder = AppSettings.Directory,
            ModelsFolder = TranscriptionModels.Root,
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

        _lastTelemetryPublish = now;
        long telemetryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var telemetry = new TelemetryDto
        {
            Running = _graph.Outputs.Any(output => output.IsRunning),
            MasterGain = Finite(OutputNode.MasterGain),
            MasterMuted = OutputNode.MasterMuted,
            MicMuted = _micMuted,
        };

        var sourceLevels = _nodes.Values
            .Where(entry => entry.Source != null)
            .ToDictionary(entry => entry.Id, entry => Meter(entry.Source!.MeterLevel));
        var sourceClipping = _nodes.Values
            .Where(entry => entry.Source != null)
            .ToDictionary(entry => entry.Id, entry => entry.Source!.Clipping);
        var outputClipping = _nodes.Values
            .Where(entry => entry.Output != null)
            .ToDictionary(entry => entry.Id, entry => entry.Output!.Clipping);
        var modifierLevels = new Dictionary<string, float>();

        foreach (var entry in _nodes.Values)
        {
            var meter = new NodeTelemetryDto();
            if (entry.Source != null)
            {
                meter.Level = entry.Source.Muted ? 0 : Meter(entry.Source.MeterLevel);
                meter.Clipping = !entry.Source.Muted && entry.Source.Clipping;
            }
            else if (entry.Output != null)
            {
                meter.Level = Meter(entry.Output.MeterLevel);
                meter.Clipping = entry.Output.Clipping;
                if (entry.Output.IsFileRecorder)
                {
                    meter.RecordingStatus = RecorderStatus(entry.Output);
                    meter.RecordingSeconds = Finite(entry.Output.RecordedSeconds);
                    meter.RecordingError = entry.Output.RecordingError;
                }
            }
            else
            {
                meter.Level = Meter(ModifierLevel(entry.Id, sourceLevels, modifierLevels, new HashSet<string>()));
                meter.Clipping = ModifierClipping(entry.Id, sourceClipping, outputClipping,
                    new HashSet<string>(), new HashSet<string>());
                if (entry.Modifier is { } modifier)
                {
                    if (modifier.Processors.OfType<DbMeterProcessor>().FirstOrDefault() is { } orientationMeter)
                    {
                        meter.DbMeterVertical = orientationMeter.IsVertical;
                    }

                    if (modifier.Enabled && modifier.Processors.OfType<WaveformProcessor>().FirstOrDefault() is { } waveform)
                    {
                        meter.Waveform = WaveformProcessor.GetSamples(waveform.TapId);
                    }

                    if (modifier.Enabled && modifier.Processors.OfType<DbMeterProcessor>().FirstOrDefault() is { } dbMeter)
                    {
                        meter.Db = DbMeterProcessor.GetDb(dbMeter.TapId);
                        meter.PeakDb = DbMeterProcessor.GetPeakDb(dbMeter.TapId);
                    }

                    if (modifier.Enabled && modifier.Processors.OfType<TranscriptionProcessor>().FirstOrDefault() is { } transcriber)
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
                            meter.TranscriptionStatus = snapshot?.Status ?? "Starting";
                            meter.Transcript = snapshot?.Transcript;
                            meter.PartialTranscript = snapshot?.Partial;
                            meter.TranscriptionError = snapshot?.Error;
                            meter.TranscriptionElapsed = snapshot?.ElapsedSeconds;
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

        _running = telemetry.Running;
        if (_settings.ShowDiagnostics)
        {
            telemetry.Diagnostics = BuildDiagnostics(now);
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
        HashSet<string> visiting)
    {
        if (cache.TryGetValue(id, out float cached)) return cached;
        if (!visiting.Add(id)) return 0;

        float level = 0;
        foreach (var link in _links)
        {
            if (link.To != id) continue;
            if (link.Kind == "source-modifier" && sourceLevels.TryGetValue(link.From, out float sourceLevel))
            {
                level = Math.Max(level, sourceLevel);
            }
            else if (link.Kind == "modifier-modifier")
            {
                level = Math.Max(level, ModifierLevel(link.From, sourceLevels, cache, visiting));
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
        HashSet<string> downstream)
    {
        if (ReachesClippingOutput(id, outputClipping, downstream)) return true;
        if (!upstream.Add(id)) return false;

        foreach (var link in _links)
        {
            if (link.To != id) continue;
            if (link.Kind == "source-modifier" &&
                sourceClipping.TryGetValue(link.From, out bool clipped) && clipped)
            {
                return true;
            }

            if (link.Kind == "modifier-modifier" &&
                ModifierClipping(link.From, sourceClipping, outputClipping, upstream, downstream))
            {
                return true;
            }
        }

        return false;
    }

    private bool ReachesClippingOutput(
        string id,
        Dictionary<string, bool> outputClipping,
        HashSet<string> visited)
    {
        if (!visited.Add(id)) return false;

        foreach (var link in _links)
        {
            if (link.From != id) continue;
            if (link.Kind == "modifier-output" &&
                outputClipping.TryGetValue(link.To, out bool clipped) && clipped)
            {
                return true;
            }

            if (link.Kind == "modifier-modifier" &&
                ReachesClippingOutput(link.To, outputClipping, visited))
            {
                return true;
            }
        }

        return false;
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

    private void ShowError(string text, Exception? ex = null)
    {
        if (ex != null)
        {
            AppLog.Error(text, ex);
        }

        SetStatus(text, isError: true);
        Post("alert", new StatusDto { Text = text, IsError = true });
    }

    private void RestoreWindowBounds()
    {
        _window.Width = _settings.Width;
        _window.Height = _settings.Height;
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top) && IsOnScreen(_settings.Left, _settings.Top))
        {
            _window.Left = _settings.Left;
            _window.Top = _settings.Top;
        }

        if (_settings.Maximized)
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

            _settings.Save();
            if (_nodes.Count == 0 && _frames.Count == 0)
            {
                DeleteSession();
            }
            else
            {
                SaveSession();
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
        _telemetryTimer.Stop();
        _appsTimer.Stop();
        _modelDownloadCancel?.Cancel();
        _modelDownloadCancel?.Dispose();
        TranscriptionRegistry.StopAll();
        SpeechModelCache.DropAll();
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
        public double X { get; set; }
        public double Y { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public string? MicHint { get; init; }
        public string? TranscriberModel { get; set; }
        public string? SourceEndpointId { get; set; }
        public string? SourceEndpointName { get; set; }
        public SourceNode? Source { get; init; }
        public OutputNode? Output { get; init; }
        public ModifierNode? Modifier { get; init; }
    }

    internal sealed class LinkEntry
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public required string From { get; init; }
        public required string To { get; init; }
        public required string Kind { get; init; }
        public float Gain { get; set; } = 1;
    }
}
