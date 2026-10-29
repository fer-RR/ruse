using System.IO;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private Preset BuildPreset()
    {
        var preset = new Preset { Name = "Ruse routing" };
        var sourceIndex = new Dictionary<string, int>();
        var outputIndex = new Dictionary<string, int>();
        var modifierIndex = new Dictionary<string, int>();

        foreach (var entry in _nodes.Values)
        {
            if (entry.Source != null)
            {
                sourceIndex[entry.Id] = preset.Sources.Count;
                preset.Sources.Add(new PresetSource
                {
                    Title = entry.Title,
                    Kind = entry.Source.Kind.ToString(),
                    ProcessId = entry.Source.ProcessId,
                    ProcessName = entry.Source.ProcessName,
                    DeviceId = entry.Source.DeviceId,
                    FilePath = entry.Source.FilePath,
                    FileLoop = entry.Source.FileLoop,
                    X = entry.X,
                    Y = entry.Y,
                    Width = entry.Width,
                    Height = entry.Height,
                    Volume = entry.Source.Gain,
                    Muted = entry.Source.Muted,
                });
            }
            else if (entry.Output != null)
            {
                outputIndex[entry.Id] = preset.Outputs.Count;
                preset.Outputs.Add(new PresetOutput
                {
                    DeviceId = entry.Output.DeviceId,
                    Title = entry.Title,
                    IsVirtual = entry.MicHint != null,
                    MicHint = entry.MicHint,
                    IsFile = entry.Output.IsFileRecorder,
                    FilePath = entry.Output.IsFileRecorder ? entry.Output.RecordingPath : null,
                    FileFormat = entry.Output.IsFileRecorder ? entry.Output.RecordingFormat.ToString() : null,
                    RecordingMode = entry.Output.RecorderMode.ToString(),
                    AutoSave = entry.Output.AutoSave,
                    X = entry.X,
                    Y = entry.Y,
                    Width = entry.Width,
                    Height = entry.Height,
                    Volume = entry.Output.Volume,
                    Muted = entry.Output.Muted,
                });
            }
            else if (entry.Modifier != null)
            {
                modifierIndex[entry.Id] = preset.Modifiers.Count;
                preset.Modifiers.Add(new PresetModifier
                {
                    Title = entry.Title,
                    Enabled = entry.Modifier.Enabled,
                    TranscriberModel = entry.TranscriberModel,
                    TranslatorTarget = entry.TranslatorTarget,
                    TranslatorModel = entry.TranslatorModel,
                    X = entry.X,
                    Y = entry.Y,
                    Width = entry.Width,
                    Height = entry.Height,
                    Processors = entry.Modifier.Processors.Select(PresetSerializer.SerializeProcessor).ToList(),
                });
            }
        }

        foreach (var link in _links)
        {
            if (link.Kind == "direct" &&
                sourceIndex.TryGetValue(link.From, out int directSource) &&
                outputIndex.TryGetValue(link.To, out int directOutput))
            {
                preset.Routes.Add(new PresetRoute
                {
                    SourceIndex = directSource,
                    OutputIndex = directOutput,
                    Gain = link.Gain,
                });
            }
            else if (link.Kind == "source-modifier" &&
                     sourceIndex.TryGetValue(link.From, out int linkedSource) &&
                     modifierIndex.TryGetValue(link.To, out int linkedModifier))
            {
                preset.SourceModifiers.Add(new PresetSourceModifierLink
                {
                    SourceIndex = linkedSource,
                    ModifierIndex = linkedModifier,
                });
            }
            else if (link.Kind == "modifier-output" &&
                     modifierIndex.TryGetValue(link.From, out int outputModifier) &&
                     outputIndex.TryGetValue(link.To, out int linkedOutput))
            {
                preset.ModifierOutputs.Add(new PresetModifierOutputLink
                {
                    ModifierIndex = outputModifier,
                    OutputIndex = linkedOutput,
                });
            }
            else if (link.Kind == "modifier-modifier" &&
                     modifierIndex.TryGetValue(link.From, out int fromModifier) &&
                     modifierIndex.TryGetValue(link.To, out int toModifier))
            {
                preset.ModifierLinks.Add(new PresetModifierLink
                {
                    FromModifierIndex = fromModifier,
                    ToModifierIndex = toModifier,
                });
            }
        }

        foreach (var frame in _frames)
        {
            preset.Frames.Add(new PresetFrame
            {
                Id = frame.Id,
                Name = frame.Name,
                X = frame.X,
                Y = frame.Y,
                Width = frame.Width,
                Height = frame.Height,
            });
        }

        return preset;
    }

    private void ApplyPreset(Preset preset, bool autoRun)
    {
        ClearGraph();
        _graph.AutoRun = autoRun;
        _frames.AddRange(preset.Frames.Select(frame => new FrameDto
        {
            Id = string.IsNullOrWhiteSpace(frame.Id) ? Guid.NewGuid().ToString("N") : frame.Id,
            Name = string.IsNullOrWhiteSpace(frame.Name) ? "Group" : frame.Name,
            X = frame.X,
            Y = frame.Y,
            Width = Math.Clamp(frame.Width, 60, 20000),
            Height = Math.Clamp(frame.Height, 40, 20000),
        }));
        var sources = new List<NodeEntry?>();
        var outputs = new List<NodeEntry?>();
        var modifiers = new List<NodeEntry?>();

        foreach (var saved in preset.Sources)
        {
            try
            {
                var kind = Enum.TryParse<SourceKind>(saved.Kind, out var parsed) ? parsed : SourceKind.Application;
                var source = new SourceNode(saved.Title, kind, saved.ProcessId, saved.DeviceId,
                    processName: saved.ProcessName, filePath: saved.FilePath, fileLoop: saved.FileLoop);
                source.Gain = saved.Volume;
                source.Muted = saved.Muted;
                var sourceEntry = RegisterSource(source, saved.X, saved.Y);
                sourceEntry.Width = saved.Width;
                sourceEntry.Height = saved.Height;
                sources.Add(sourceEntry);
            }
            catch (Exception ex)
            {
                sources.Add(null);
                AppLog.Warning($"Skipped source '{saved.Title}': {ex.Message}");
            }
        }

        foreach (var saved in preset.Outputs)
        {
            try
            {
                OutputNode output;
                string? micHint = null;
                if (saved.IsFile)
                {
                    output = OutputNode.CreateForFile(saved.FilePath ?? string.Empty, AudioFileFormats.Parse(saved.FileFormat));
                    output.AutoStart = false;
                }
                else if (AsioCatalog.IsAsioId(saved.DeviceId))
                {
                    output = new OutputNode(FindAsioDriver(saved.DeviceId));
                }
                else if (saved.IsVirtual)
                {
                    var info = VirtualDeviceCatalog.ResolveRender(saved.DeviceId, saved.Title, saved.MicHint);
                    micHint = info.CaptureFriendlyName;
                    using var enumerator = new MMDeviceEnumerator();
                    output = new OutputNode(enumerator.GetDevice(info.RenderDeviceId));
                }
                else
                {
                    using var enumerator = new MMDeviceEnumerator();
                    output = new OutputNode(enumerator.GetDevice(saved.DeviceId));
                }

                output.Volume = saved.Volume;
                output.Muted = saved.Muted;
                output.RecorderMode = Enum.TryParse<RecorderMode>(saved.RecordingMode, true, out var mode)
                    ? mode
                    : RecorderMode.Off;
                output.AutoSave = saved.AutoSave;
                if (!output.AutoSave && output.RecorderMode != RecorderMode.Off)
                {
                    output.RecorderMode = RecorderMode.Off;
                }
                var outputEntry = RegisterOutput(output, saved.X, saved.Y, micHint);
                outputEntry.Width = saved.Width;
                outputEntry.Height = saved.Height;
                outputs.Add(outputEntry);
            }
            catch (Exception ex)
            {
                outputs.Add(null);
                AppLog.Warning($"Skipped output '{saved.Title}': {ex.Message}");
            }
        }

        foreach (var saved in preset.Modifiers)
        {
            var modifier = new ModifierNode(saved.Title);
            foreach (var processor in saved.Processors)
            {
                var created = PresetSerializer.CreateProcessor(processor);
                if (created != null) modifier.Add(created);
            }

            if (modifier.Count == 0 && AudioProcessorCatalog.Create(GainProcessor.Id) is { } fallback)
            {
                modifier.Add(fallback);
            }

            modifier.Enabled = saved.Enabled;
            bool isChain = modifier.Processors.Any(processor => processor is ChainProcessor);
            var modifierEntry = RegisterModifier(modifier, saved.X, saved.Y,
                flavor: isChain ? "chain" : "effect",
                subtitle: isChain ? "Saved chain" : "Processing",
                transcriberModel: saved.TranscriberModel,
                translatorTarget: saved.TranslatorTarget,
                translatorModel: saved.TranslatorModel);
            modifierEntry.Width = saved.Width;
            modifierEntry.Height = saved.Height;
            modifiers.Add(modifierEntry);
        }

        foreach (var route in preset.Routes)
        {
            if (Resolve(sources, route.SourceIndex) is { } source && Resolve(outputs, route.OutputIndex) is { } output)
            {
                ConnectEntries(source, output, route.Gain);
            }
        }

        foreach (var link in preset.SourceModifiers)
        {
            if (Resolve(sources, link.SourceIndex) is { } source && Resolve(modifiers, link.ModifierIndex) is { } modifier)
            {
                ConnectEntries(source, modifier);
            }
        }

        foreach (var link in preset.ModifierLinks)
        {
            if (Resolve(modifiers, link.FromModifierIndex) is { } from && Resolve(modifiers, link.ToModifierIndex) is { } to)
            {
                ConnectEntries(from, to);
            }
        }

        foreach (var link in preset.ModifierOutputs)
        {
            if (Resolve(modifiers, link.ModifierIndex) is { } modifier && Resolve(outputs, link.OutputIndex) is { } output)
            {
                ConnectEntries(modifier, output);
            }
        }

        if (autoRun)
        {
            StartRouting();
        }
        else
        {
            _running = false;
            PublishGraph();
        }
    }

    private static NodeEntry? Resolve(List<NodeEntry?> entries, int index) =>
        index >= 0 && index < entries.Count ? entries[index] : null;

    private void SaveSession()
    {
        if (App.DemoMode || (_nodes.Count == 0 && _frames.Count == 0))
        {
            return;
        }

        Directory.CreateDirectory(AppSettings.Directory);
        File.WriteAllText(AppSettings.SessionPath, BuildPreset().ToJson());
    }

    private void TryLoadSession()
    {
        try
        {
            if (!File.Exists(AppSettings.SessionPath))
            {
                PublishGraph();
                return;
            }

            ApplyPreset(Preset.FromJson(File.ReadAllText(AppSettings.SessionPath)), autoRun: false);
            SetStatus("Restored last session · routing is idle.");
        }
        catch (Exception ex)
        {
            ShowError("Could not restore the last session: " + ex.Message, ex);
        }
    }

    private void DeleteSession()
    {
        if (App.DemoMode)
        {
            return;
        }

        try
        {
            if (File.Exists(AppSettings.SessionPath))
            {
                File.Delete(AppSettings.SessionPath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not clear the saved session: " + ex.Message);
        }
    }

    private void SavePresetDialog()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save routing preset",
            Filter = "Ruse preset (*.ruse.json)|*.ruse.json|JSON (*.json)|*.json|All files (*.*)|*.*",
            FileName = "routing.ruse.json",
        };
        if (dialog.ShowDialog(_window) != true) return;

        File.WriteAllText(dialog.FileName, BuildPreset().ToJson());
        SetStatus($"Preset saved: {Path.GetFileName(dialog.FileName)}");
    }

    private void LoadPresetDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load routing preset",
            Filter = "Ruse preset (*.ruse.json)|*.ruse.json|JSON (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(_window) != true) return;

        ClearHistory();
        ApplyPreset(Preset.FromJson(File.ReadAllText(dialog.FileName)), autoRun: true);
        SetStatus($"Preset loaded: {Path.GetFileName(dialog.FileName)}");
    }

    private void ApplySettings(SettingsDto settings)
    {
        string oldModel = _settings.TranscriberModel;
        string oldTranslatorModel = _settings.TranslatorModel;
        string oldTranslatorTarget = _settings.TranslatorTarget;
        string oldTranslationProvider = _settings.TranslationProvider;
        _settings.AutoLoadSession = settings.AutoLoadSession;
        _settings.FollowPlayingApps = settings.FollowPlayingApps;
        _settings.FollowOutputId = settings.FollowOutputId ?? "";
        _settings.UiLanguage = settings.UiLanguage is "pt-BR" or "es" or "fr" or "de" or "it" or "ja" or "ko" or "zh-CN"
            ? settings.UiLanguage
            : "en";
        _settings.Theme = settings.Theme is "light" ? "light" : "dark";
        ApplyWindowTheme();
        _settings.StartRoutingOnLaunch = settings.StartRoutingOnLaunch;
        _settings.HighlightMutedNodes = settings.HighlightMutedNodes;
        _settings.SignalGlow = settings.SignalGlow;
        _settings.SnapToGrid = settings.SnapToGrid;
        _settings.HighlightCableOnHover = settings.HighlightCableOnHover;
        _settings.CableAnimation = settings.CableAnimation;
        _settings.ConnectorAnimation = settings.ConnectorAnimation;
        _settings.MinimapMode = Math.Clamp(settings.MinimapMode, 0, 2);
        _settings.ShowWelcomeScreen = settings.ShowWelcomeScreen;
        bool trayChanged = _settings.MinimizeToTray != settings.MinimizeToTray;
        _settings.MinimizeToTray = settings.MinimizeToTray;
        if (trayChanged)
        {
            _settings.TrayPromptShown = true;
        }

        _settings.ShowDiagnostics = settings.ShowDiagnostics;
        bool gpuChanged = _settings.UseGpu != settings.UseGpu;
        _settings.UseGpu = settings.UseGpu;
        ModelRuntime.UseGpu = _settings.UseGpu;
        _settings.IsolateVst3 = settings.IsolateVst3;
        Vst3ProcessorFactory.UseIsolation = _settings.IsolateVst3;
        if (gpuChanged)
        {
            ModelRuntime.ClearGpuFallback();
        }
        _settings.TranscriberModel = TranscriptionModels.Find(settings.TranscriberModel)?.Id ?? TranscriptionModels.DefaultId;
        _settings.TranslatorModel = TranslationModels.Find(settings.TranslatorModel)?.Id ?? TranslationModels.DefaultId;
        _settings.TranslatorTarget = TranslationLanguages.Normalize(settings.TranslatorTarget);
        _settings.TranslationProvider = string.Equals(settings.TranslationProvider, "opus", StringComparison.OrdinalIgnoreCase)
            ? "opus" : "qwen";
        StartupRegistration.SetEnabled(settings.StartWithWindows);
        _settings.Save();

        // App-follow should notice a newly playing app as it starts, not up to
        // four seconds later when the first sound is already gone.
        _appsTimer.Interval = TimeSpan.FromMilliseconds(_settings.FollowPlayingApps ? 750 : 4000);
        if (_settings.FollowPlayingApps)
        {
            RefreshApps();
        }

        if (gpuChanged)
        {
            SpeechModelCache.DropAll();
            TranslationModelCache.DropAll();
            TranslationModelProbe.Clear();
            RestartSpeechSessions();
        }

        string newModel = TranscriptionModels.Resolve(_settings.TranscriberModel);
        if (!string.Equals(oldModel, newModel, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var entry in _nodes.Values.Where(entry =>
                         (entry.Modifier?.Processors.OfType<TranscriptionProcessor>().Any() == true ||
                          entry.Modifier?.Processors.OfType<TranslatorProcessor>().Any() == true) &&
                         (string.IsNullOrEmpty(entry.TranscriberModel) ||
                          string.Equals(entry.TranscriberModel, oldModel, StringComparison.OrdinalIgnoreCase))))
            {
                ApplyTranscriberModel(entry, newModel);
            }
        }

        string newTranslatorModel = TranslationModels.Resolve(_settings.TranslatorModel);
        string newTranslatorTarget = TranslationLanguages.Normalize(_settings.TranslatorTarget);
        bool providerChanged = !string.Equals(
            oldTranslationProvider, _settings.TranslationProvider, StringComparison.OrdinalIgnoreCase);
        foreach (var entry in _nodes.Values.Where(entry =>
                     entry.Modifier?.Processors.OfType<TranslatorProcessor>().Any() == true))
        {
            bool modelChanged = !string.Equals(entry.TranslatorModel, newTranslatorModel, StringComparison.OrdinalIgnoreCase) &&
                                (string.IsNullOrEmpty(entry.TranslatorModel) ||
                                 string.Equals(entry.TranslatorModel, oldTranslatorModel, StringComparison.OrdinalIgnoreCase));
            bool targetChanged = !string.Equals(entry.TranslatorTarget, newTranslatorTarget, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(entry.TranslatorTarget, oldTranslatorTarget, StringComparison.OrdinalIgnoreCase);
            if (modelChanged || targetChanged || providerChanged)
            {
                ApplyTranslatorSettings(entry,
                    modelChanged ? newTranslatorModel : entry.TranslatorModel,
                    targetChanged ? newTranslatorTarget : entry.TranslatorTarget);
            }
        }

        if (trayChanged)
        {
            _window.SyncTrayIcon();
        }

        PublishSettings();
        PublishGraph();
        SetStatus("Settings saved.");
    }

    /// <summary>Reopens speech sessions so a backend change takes effect immediately.</summary>
    private void RestartSpeechSessions()
    {
        foreach (var entry in _nodes.Values)
        {
            if (entry.Modifier == null)
            {
                continue;
            }

            foreach (var transcriber in entry.Modifier.Processors.OfType<TranscriptionProcessor>())
            {
                TranscriptionRegistry.Stop(transcriber.TapId);
                if (!string.IsNullOrEmpty(entry.TranscriberModel))
                {
                    TranscriptionRegistry.Ensure(transcriber.TapId, entry.TranscriberModel);
                }
            }

            foreach (var translator in entry.Modifier.Processors.OfType<TranslatorProcessor>())
            {
                TranslationRegistry.Stop(translator.TapId);
                if (!string.IsNullOrEmpty(entry.TranscriberModel))
                {
                    TranslationRegistry.Ensure(
                        translator.TapId,
                        entry.TranscriberModel,
                        entry.TranslatorModel ?? "",
                        entry.TranslatorTarget ?? TranslationLanguages.DefaultCode,
                        _settings.TranslationProvider);
                }
            }
        }
    }

    private void ApplyTranscriberModel(NodeEntry entry, string? modelId)
    {
        if (entry.Modifier == null)
        {
            return;
        }

        bool hasTranslator = entry.Modifier.Processors.OfType<TranslatorProcessor>().Any();
        string? translatorSpeechModel = hasTranslator
            ? TranscriptionModels.ResolveMultilingual(modelId)
            : null;
        entry.TranscriberModel = hasTranslator ? translatorSpeechModel : modelId;
        foreach (var transcriber in entry.Modifier.Processors.OfType<TranscriptionProcessor>())
        {
            TranscriptionRegistry.Stop(transcriber.TapId);
            if (!string.IsNullOrEmpty(modelId))
            {
                TranscriptionRegistry.Ensure(transcriber.TapId, modelId);
            }
        }

        foreach (var translator in entry.Modifier.Processors.OfType<TranslatorProcessor>())
        {
            TranslationRegistry.Stop(translator.TapId);
            if (!string.IsNullOrEmpty(translatorSpeechModel))
            {
                TranslationRegistry.Ensure(
                    translator.TapId,
                    translatorSpeechModel,
                    entry.TranslatorModel ?? "",
                    entry.TranslatorTarget ?? TranslationLanguages.DefaultCode,
                    _settings.TranslationProvider);
            }
        }
    }

    private void ApplyTranslatorSettings(NodeEntry entry, string? translationModel, string? target)
    {
        if (entry.Modifier == null)
        {
            return;
        }

        entry.TranslatorTarget = TranslationLanguages.Normalize(target);
        entry.TranslatorModel = TranslationLanguages.NeedsModel(entry.TranslatorTarget)
            ? TranslationModels.Resolve(translationModel)
            : null;
        string speechModel = TranscriptionModels.ResolveMultilingual(entry.TranscriberModel);
        entry.TranscriberModel = string.IsNullOrEmpty(speechModel) ? null : speechModel;

        foreach (var translator in entry.Modifier.Processors.OfType<TranslatorProcessor>())
        {
            TranslationRegistry.Stop(translator.TapId);
            if (!string.IsNullOrEmpty(speechModel))
            {
                TranslationRegistry.Ensure(
                    translator.TapId,
                    speechModel,
                    entry.TranslatorModel ?? "",
                    entry.TranslatorTarget,
                    _settings.TranslationProvider);
            }
        }
    }

    private void SetTranscriberModel(string id, string modelId)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<TranscriptionProcessor>().Any() != true)
        {
            return;
        }

        if (!TranscriptionModels.IsInstalled(modelId))
        {
            SetStatus("That speech model is not installed.", isError: true);
            return;
        }

        ApplyTranscriberModel(entry, modelId);
        SetStatus($"{entry.Title}: using {TranscriptionModels.Find(modelId)?.Name ?? modelId}.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private async void DownloadTranscriberModel(string modelId)
    {
        var model = TranscriptionModels.Find(modelId);
        if (model == null) return;
        if (_modelDownloadCancel != null)
        {
            SetStatus("Another speech model is already downloading.", isError: true);
            return;
        }

        var cancel = new CancellationTokenSource();
        _modelDownloadCancel = cancel;
        var progress = new Progress<TranscriberModelProgress>(PublishModelProgress);
        try
        {
            await TranscriptionModels.DownloadAsync(modelId, progress, cancel.Token);
            ModelProbe.Clear();
            if (string.IsNullOrEmpty(TranscriptionModels.Resolve(_settings.TranscriberModel)))
            {
                _settings.TranscriberModel = modelId;
                _settings.Save();
            }

            foreach (var entry in _nodes.Values.Where(entry =>
                         (entry.Modifier?.Processors.OfType<TranscriptionProcessor>().Any() == true ||
                          entry.Modifier?.Processors.OfType<TranslatorProcessor>().Any() == true) &&
                         (string.IsNullOrEmpty(entry.TranscriberModel) ||
                          string.Equals(entry.TranscriberModel, modelId, StringComparison.OrdinalIgnoreCase))))
            {
                ApplyTranscriberModel(entry, modelId);
            }

            PublishSettings();
            PublishGraph();
            PublishTelemetry(force: true);
            SetStatus($"{model.Name} installed.");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{model.Name} download cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus($"{model.Name} download failed: {ex.Message}", isError: true);
        }
        finally
        {
            _modelDownloadCancel = null;
            cancel.Dispose();
            PublishSettings();
        }
    }

    private void CancelTranscriberDownload()
    {
        _modelDownloadCancel?.Cancel();
    }

    private void PublishModelProgress(TranscriberModelProgress progress)
    {
        _modelDownload = progress;
        long now = Environment.TickCount64;
        if (progress.Phase is "done" or "error" or "cancelled" || now - _lastModelPublish >= 150)
        {
            _lastModelPublish = now;
            PublishSettings();
        }

        if (progress.Phase is "done" or "error" or "cancelled")
        {
            SetStatus(progress.Message, isError: progress.Phase == "error");
        }
        else if (now - _lastModelStatus >= 1000)
        {
            _lastModelStatus = now;
            SetStatus(progress.Message);
        }
    }

    private void DeleteTranscriberModel(string modelId)
    {
        var model = TranscriptionModels.Find(modelId);
        if (model == null) return;

        try
        {
            TranscriptionRegistry.StopModel(modelId);
            TranslationRegistry.StopSpeechModel(modelId);
            SpeechModelCache.Drop(modelId);
            TranscriptionModels.Delete(modelId);
            ModelProbe.Clear();

            string fallback = TranscriptionModels.Resolve(
                string.Equals(_settings.TranscriberModel, modelId, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : _settings.TranscriberModel);
            if (string.Equals(_settings.TranscriberModel, modelId, StringComparison.OrdinalIgnoreCase))
            {
                _settings.TranscriberModel = fallback;
                _settings.Save();
            }

            foreach (var entry in _nodes.Values.Where(entry =>
                         (entry.Modifier?.Processors.OfType<TranscriptionProcessor>().Any() == true ||
                          entry.Modifier?.Processors.OfType<TranslatorProcessor>().Any() == true) &&
                         string.Equals(entry.TranscriberModel, modelId, StringComparison.OrdinalIgnoreCase)))
            {
                ApplyTranscriberModel(entry, fallback);
            }

            PublishSettings();
            PublishGraph();
            PublishTelemetry(force: true);
            SetStatus($"{model.Name} removed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not remove {model.Name}: {ex.Message}", isError: true);
            AppLog.Error($"Could not delete speech model '{modelId}'.", ex);
        }
    }

    private void ClearTranscription(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        foreach (var transcriber in entry.Modifier.Processors.OfType<TranscriptionProcessor>())
        {
            TranscriptionRegistry.Clear(transcriber.TapId);
        }

        SetStatus($"{entry.Title}: transcript cleared.");
        PublishTelemetry(force: true);
    }

    private void RestartTranscription(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        string modelId = string.IsNullOrEmpty(entry.TranscriberModel)
            ? TranscriptionModels.Resolve(_settings.TranscriberModel)
            : entry.TranscriberModel;
        if (string.IsNullOrEmpty(modelId))
        {
            SetStatus("Install a speech model before retrying.", isError: true);
            return;
        }

        entry.TranscriberModel = modelId;
        ModelProbe.Invalidate(TranscriptionModels.GetInstalledPath(modelId));
        foreach (var transcriber in entry.Modifier.Processors.OfType<TranscriptionProcessor>())
        {
            TranscriptionRegistry.Restart(transcriber.TapId, modelId);
        }

        SetStatus($"{entry.Title}: transcription restarted.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void ExportTranscription(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        var transcriber = entry.Modifier.Processors.OfType<TranscriptionProcessor>().FirstOrDefault();
        if (transcriber == null) return;

        var snapshot = TranscriptionRegistry.Snapshot(transcriber.TapId, includeCues: true);
        string text = snapshot?.Transcript?.Trim() ?? "";
        if (text.Length == 0)
        {
            SetStatus("There is no transcript to export yet.", isError: true);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export transcript",
            Filter = "SubRip subtitles (*.srt)|*.srt|WebVTT subtitles (*.vtt)|*.vtt|Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = SanitizeFileName(entry.Title) + ".srt",
            DefaultExt = "srt",
            AddExtension = true,
            FilterIndex = 1,
        };
        if (dialog.ShowDialog(_window) != true) return;

        int filter = dialog.FilterIndex;
        string extension = filter switch
        {
            1 => ".srt",
            2 => ".vtt",
            3 => ".txt",
            _ => Path.GetExtension(dialog.FileName).ToLowerInvariant(),
        };
        string path = filter is 1 or 2 or 3
            ? Path.ChangeExtension(dialog.FileName, extension)
            : dialog.FileName;
        if (extension is ".srt" or ".vtt")
        {
            var cues = snapshot?.Segments is { Count: > 0 } segments
                ? segments
                : new[] { new SubtitleCue(0, 1, text) };
            File.WriteAllText(path,
                extension == ".vtt" ? SubtitleExporter.Vtt(cues) : SubtitleExporter.Srt(cues));
        }
        else
        {
            File.WriteAllText(path,
                $"{entry.Title}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}{Environment.NewLine}" +
                text + Environment.NewLine);
        }

        SetStatus($"Transcript saved: {Path.GetFileName(path)}");
    }

    private void SetTranslatorTarget(string id, string target)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<TranslatorProcessor>().Any() != true)
        {
            return;
        }

        string code = TranslationLanguages.Normalize(target);
        ApplyTranslatorSettings(entry, entry.TranslatorModel, code);
        var language = TranslationLanguages.Find(code);
        SetStatus($"{entry.Title}: translating to {language?.Label ?? code}.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetTranslatorModel(string id, string modelId)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<TranslatorProcessor>().Any() != true)
        {
            return;
        }

        if (!TranslationModels.IsInstalled(modelId))
        {
            SetStatus("That translation model is not installed.", isError: true);
            return;
        }

        ApplyTranslatorSettings(entry, modelId, entry.TranslatorTarget);
        SetStatus($"{entry.Title}: using {TranslationModels.Find(modelId)?.Name ?? modelId}.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private async void DownloadTranslationModel(string modelId)
    {
        var model = TranslationModels.Find(modelId);
        if (model == null) return;
        if (_translationDownloadCancel != null)
        {
            SetStatus("Another translation model is already downloading.", isError: true);
            return;
        }

        var cancel = new CancellationTokenSource();
        _translationDownloadCancel = cancel;
        var progress = new Progress<TranslationModelProgress>(PublishTranslationProgress);
        try
        {
            await TranslationModels.DownloadAsync(modelId, progress, cancel.Token);
            TranslationModelProbe.Clear();
            if (string.IsNullOrEmpty(TranslationModels.Resolve(_settings.TranslatorModel)))
            {
                _settings.TranslatorModel = modelId;
                _settings.Save();
            }

            foreach (var entry in _nodes.Values.Where(entry =>
                         entry.Modifier?.Processors.OfType<TranslatorProcessor>().Any() == true &&
                         (string.IsNullOrEmpty(entry.TranslatorModel) ||
                          string.Equals(entry.TranslatorModel, modelId, StringComparison.OrdinalIgnoreCase))))
            {
                ApplyTranslatorSettings(entry, modelId, entry.TranslatorTarget);
            }

            PublishSettings();
            PublishGraph();
            PublishTelemetry(force: true);
            SetStatus($"{model.Name} installed.");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{model.Name} download cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus($"{model.Name} download failed: {ex.Message}", isError: true);
        }
        finally
        {
            _translationDownloadCancel = null;
            cancel.Dispose();
            PublishSettings();
        }
    }

    private async void DownloadOpusTranslationModel(string modelId)
    {
        var model = OpusTranslationModels.Find(modelId);
        if (model == null) return;
        if (_translationDownloadCancel != null)
        {
            SetStatus("Another translation model is already downloading.", isError: true);
            return;
        }

        var cancel = new CancellationTokenSource();
        _translationDownloadCancel = cancel;
        var progress = new Progress<TranslationModelProgress>(PublishTranslationProgress);
        try
        {
            await OpusTranslationModels.DownloadAsync(modelId, progress, cancel.Token);
            RestartTranslationNodes();
            PublishSettings();
            PublishGraph();
            PublishTelemetry(force: true);
            SetStatus($"{model.Name} installed.");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{model.Name} download cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus($"{model.Name} download failed: {ex.Message}", isError: true);
        }
        finally
        {
            _translationDownloadCancel = null;
            cancel.Dispose();
            PublishSettings();
        }
    }

    private void DeleteOpusTranslationModel(string modelId)
    {
        var model = OpusTranslationModels.Find(modelId);
        if (model == null) return;
        try
        {
            OpusMtEngine.Drop(modelId);
            OpusTranslationModels.Delete(modelId);
            RestartTranslationNodes();
            PublishSettings();
            PublishGraph();
            PublishTelemetry(force: true);
            SetStatus($"{model.Name} removed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not remove {model.Name}: {ex.Message}", isError: true);
            AppLog.Error($"Could not delete OPUS-MT model '{modelId}'.", ex);
        }
    }

    private void RestartTranslationNodes()
    {
        foreach (var entry in _nodes.Values.Where(entry =>
                     entry.Modifier?.Processors.OfType<TranslatorProcessor>().Any() == true))
        {
            ApplyTranslatorSettings(entry, entry.TranslatorModel, entry.TranslatorTarget);
        }
    }

    private void CancelTranslationDownload()
    {
        _translationDownloadCancel?.Cancel();
    }

    private void PublishTranslationProgress(TranslationModelProgress progress)
    {
        _translationDownload = progress;
        long now = Environment.TickCount64;
        if (progress.Phase is "done" or "error" or "cancelled" || now - _lastTranslationPublish >= 150)
        {
            _lastTranslationPublish = now;
            PublishSettings();
        }

        if (progress.Phase is "done" or "error" or "cancelled")
        {
            SetStatus(progress.Message, isError: progress.Phase == "error");
        }
        else if (now - _lastTranslationStatus >= 1000)
        {
            _lastTranslationStatus = now;
            SetStatus(progress.Message);
        }
    }

    private void DeleteTranslationModel(string modelId)
    {
        var model = TranslationModels.Find(modelId);
        if (model == null) return;

        try
        {
            TranslationRegistry.StopTranslationModel(modelId);
            TranslationModelCache.Drop(modelId);
            TranslationModels.Delete(modelId);
            TranslationModelProbe.Clear();

            string fallback = TranslationModels.Resolve(
                string.Equals(_settings.TranslatorModel, modelId, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : _settings.TranslatorModel);
            if (string.Equals(_settings.TranslatorModel, modelId, StringComparison.OrdinalIgnoreCase))
            {
                _settings.TranslatorModel = fallback;
                _settings.Save();
            }

            foreach (var entry in _nodes.Values.Where(entry =>
                         entry.Modifier?.Processors.OfType<TranslatorProcessor>().Any() == true &&
                         string.Equals(entry.TranslatorModel, modelId, StringComparison.OrdinalIgnoreCase)))
            {
                ApplyTranslatorSettings(entry, fallback, entry.TranslatorTarget);
            }

            PublishSettings();
            PublishGraph();
            PublishTelemetry(force: true);
            SetStatus($"{model.Name} removed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not remove {model.Name}: {ex.Message}", isError: true);
            AppLog.Error($"Could not delete translation model '{modelId}'.", ex);
        }
    }

    private void ClearTranslation(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        foreach (var translator in entry.Modifier.Processors.OfType<TranslatorProcessor>())
        {
            TranslationRegistry.Clear(translator.TapId);
        }

        SetStatus($"{entry.Title}: translation cleared.");
        PublishTelemetry(force: true);
    }

    private void RestartTranslation(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        string speechModel = TranscriptionModels.ResolveMultilingual(
            string.IsNullOrEmpty(entry.TranscriberModel)
                ? _settings.TranscriberModel
                : entry.TranscriberModel);
        string translationModel = string.IsNullOrEmpty(entry.TranslatorModel)
            ? TranslationModels.Resolve(_settings.TranslatorModel)
            : entry.TranslatorModel;
        string target = TranslationLanguages.Normalize(entry.TranslatorTarget);

        if (string.IsNullOrEmpty(speechModel))
        {
            SetStatus("Install a multilingual speech model before retrying.", isError: true);
            return;
        }

        if (TranslationLanguages.NeedsModel(target) &&
            _settings.TranslationProvider != "opus" &&
            string.IsNullOrEmpty(translationModel))
        {
            SetStatus("Install a translation model before retrying.", isError: true);
            return;
        }

        entry.TranscriberModel = speechModel;
        entry.TranslatorModel = TranslationLanguages.NeedsModel(target) ? translationModel : null;
        ModelProbe.Invalidate(TranscriptionModels.GetInstalledPath(speechModel));
        TranslationModelProbe.Invalidate(TranslationModels.GetInstalledPath(translationModel));
        foreach (var translator in entry.Modifier.Processors.OfType<TranslatorProcessor>())
        {
            TranslationRegistry.Restart(
                translator.TapId,
                speechModel,
                entry.TranslatorModel ?? "",
                target,
                _settings.TranslationProvider);
        }

        SetStatus($"{entry.Title}: translation restarted.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void ExportTranslation(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        var translator = entry.Modifier.Processors.OfType<TranslatorProcessor>().FirstOrDefault();
        if (translator == null) return;

        var snapshot = TranslationRegistry.Snapshot(translator.TapId, includeCues: true);
        string source = snapshot?.SourceText?.Trim() ?? "";
        string translated = snapshot?.TranslatedText?.Trim() ?? "";
        if (source.Length == 0 && translated.Length == 0)
        {
            SetStatus("There is no translation to export yet.", isError: true);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export translation",
            Filter = "SubRip subtitles (*.srt)|*.srt|WebVTT subtitles (*.vtt)|*.vtt|Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = SanitizeFileName(entry.Title) + ".srt",
            DefaultExt = "srt",
            AddExtension = true,
            FilterIndex = 1,
        };
        if (dialog.ShowDialog(_window) != true) return;

        int filter = dialog.FilterIndex;
        string extension = filter switch
        {
            1 => ".srt",
            2 => ".vtt",
            3 => ".txt",
            _ => Path.GetExtension(dialog.FileName).ToLowerInvariant(),
        };
        string path = filter is 1 or 2 or 3
            ? Path.ChangeExtension(dialog.FileName, extension)
            : dialog.FileName;
        if (extension is ".srt" or ".vtt")
        {
            if (translated.Length == 0)
            {
                SetStatus("There is no translated text to export yet.", isError: true);
                return;
            }

            var cues = snapshot?.TranslatedSegments is { Count: > 0 } segments
                ? segments
                : new[] { new SubtitleCue(0, 1, translated) };
            File.WriteAllText(path,
                extension == ".vtt" ? SubtitleExporter.Vtt(cues) : SubtitleExporter.Srt(cues));
        }
        else
        {
            string targetLabel = TranslationLanguages.Find(entry.TranslatorTarget)?.Label ?? entry.TranslatorTarget ?? "";
            File.WriteAllText(path,
                $"{entry.Title}{Environment.NewLine}" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}{Environment.NewLine}" +
                $"Original{Environment.NewLine}{source}{Environment.NewLine}{Environment.NewLine}" +
                $"{targetLabel}{Environment.NewLine}{translated}{Environment.NewLine}");
        }

        SetStatus($"Translation saved: {Path.GetFileName(path)}");
    }

    private void OpenTranslationModelsFolder()
    {
        try
        {
            Directory.CreateDirectory(TranslationModels.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = TranslationModels.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open the translation models folder: " + ex.Message, isError: true);
        }
    }

    private void CopyDiagnostics()
    {
        int installedCount = TranscriptionModels.All.Count(model => TranscriptionModels.IsInstalled(model.Id));
        long installedBytes = TranscriptionModels.All
            .Where(model => TranscriptionModels.IsInstalled(model.Id))
            .Sum(model => TranscriptionModels.GetInstalledSizeBytes(model.Id));
        int installedTranslationCount = TranslationModels.All.Count(model => TranslationModels.IsInstalled(model.Id));
        long installedTranslationBytes = TranslationModels.All
            .Where(model => TranslationModels.IsInstalled(model.Id))
            .Sum(model => TranslationModels.GetInstalledSizeBytes(model.Id));
        string version = typeof(WebShellController).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var lines = new[]
        {
            $"Ruse {version}",
            $"OS: {Environment.OSVersion.VersionString}",
            $".NET: {Environment.Version}",
            $"WebView2: {(_webView2Version.Length == 0 ? "not initialized" : _webView2Version)}",
            $"WPF render tier: {System.Windows.Media.RenderCapability.Tier >> 16}",
            $"Data: {AppSettings.Directory}",
            $"Models: {TranscriptionModels.Root}",
            $"Installed models: {installedCount} ({TranscriptionModels.FormatSize(installedBytes)})",
            $"Default model: {_settings.TranscriberModel}",
            $"Translation models: {installedTranslationCount} ({TranslationModels.FormatSize(installedTranslationBytes)})",
            $"Default translation: {_settings.TranslatorModel} -> {_settings.TranslatorTarget}",
            $"GPU: {(ModelRuntime.GpuAvailable ? ModelRuntime.GpuDeviceName : ModelRuntime.GpuStatusError ?? "not available")}",
            $"GPU fallback: {ModelRuntime.GpuFallbackReason ?? "none"}",
            $"Output underruns: {_graph.Outputs.Where(output => !output.IsMonitor).Sum(output => output.Underruns)}",
            $"Nodes: {_nodes.Count} · links: {_links.Count} · running: {_running}",
        };

        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
            SetStatus("Diagnostics copied to the clipboard.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not copy diagnostics: " + ex.Message, isError: true);
        }
    }

    private void OpenSoundSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:sound",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open Windows sound settings: " + ex.Message, isError: true);
        }
    }

    private void OpenTranscriberModelsFolder()
    {
        Directory.CreateDirectory(TranscriptionModels.Root);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = TranscriptionModels.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("Could not open the models folder: " + ex.Message, isError: true);
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        return string.IsNullOrWhiteSpace(name) ? "transcript" : name.Trim();
    }
}
