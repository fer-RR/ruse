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
                    DeviceId = entry.Source.DeviceId,
                    X = entry.X,
                    Y = entry.Y,
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
                    X = entry.X,
                    Y = entry.Y,
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
                    X = entry.X,
                    Y = entry.Y,
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

        return preset;
    }

    private void ApplyPreset(Preset preset, bool autoRun)
    {
        ClearGraph();
        _graph.AutoRun = autoRun;
        var sources = new List<NodeEntry?>();
        var outputs = new List<NodeEntry?>();
        var modifiers = new List<NodeEntry?>();

        foreach (var saved in preset.Sources)
        {
            try
            {
                var kind = Enum.TryParse<SourceKind>(saved.Kind, out var parsed) ? parsed : SourceKind.Application;
                var source = new SourceNode(saved.Title, kind, saved.ProcessId, saved.DeviceId);
                source.Gain = saved.Volume;
                source.Muted = saved.Muted;
                sources.Add(RegisterSource(source, saved.X, saved.Y));
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
                outputs.Add(RegisterOutput(output, saved.X, saved.Y, micHint));
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
            modifiers.Add(RegisterModifier(modifier, saved.X, saved.Y,
                flavor: isChain ? "chain" : "effect",
                subtitle: isChain ? "Saved chain" : "Processing",
                transcriberModel: saved.TranscriberModel));
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
        if (App.DemoMode || App.UiTestMode || _nodes.Count == 0)
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
        if (App.DemoMode || App.UiTestMode)
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
        _settings.StartRoutingOnLaunch = settings.StartRoutingOnLaunch;
        _settings.HighlightMutedNodes = settings.HighlightMutedNodes;
        _settings.SignalGlow = settings.SignalGlow;
        _settings.HighlightCableOnHover = settings.HighlightCableOnHover;
        _settings.MinimapMode = Math.Clamp(settings.MinimapMode, 0, 2);
        _settings.TranscriberModel = TranscriptionModels.Find(settings.TranscriberModel)?.Id ?? oldModel;
        StartupRegistration.SetEnabled(settings.StartWithWindows);
        _settings.Save();

        string newModel = TranscriptionModels.Resolve(_settings.TranscriberModel);
        foreach (var entry in _nodes.Values.Where(entry =>
                     entry.Modifier?.Processors.OfType<TranscriptionProcessor>().Any() == true &&
                     (string.IsNullOrEmpty(entry.TranscriberModel) ||
                      string.Equals(entry.TranscriberModel, oldModel, StringComparison.OrdinalIgnoreCase))))
        {
            ApplyTranscriberModel(entry, newModel);
        }

        PublishSettings();
        PublishGraph();
        SetStatus("Settings saved.");
    }

    private void ApplyTranscriberModel(NodeEntry entry, string? modelId)
    {
        if (entry.Modifier?.Processors.OfType<TranscriptionProcessor>().FirstOrDefault() is not { } transcriber)
        {
            return;
        }

        entry.TranscriberModel = modelId;
        TranscriptionRegistry.Stop(transcriber.TapId);
        if (!string.IsNullOrEmpty(modelId))
        {
            TranscriptionRegistry.Ensure(transcriber.TapId, modelId);
        }
    }

    private void SetTranscriberModel(string id, string modelId)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<TranscriptionProcessor>().Any() != true)
        {
            return;
        }

        string resolved = TranscriptionModels.Resolve(modelId);
        if (string.IsNullOrEmpty(resolved))
        {
            SetStatus("That speech model is not installed.", isError: true);
            return;
        }

        ApplyTranscriberModel(entry, resolved);
        SetStatus($"{entry.Title}: using {TranscriptionModels.Find(resolved)?.Name ?? resolved}.");
        PublishGraph();
    }

    private async void DownloadTranscriberModel(string modelId)
    {
        var model = TranscriptionModels.Find(modelId);
        if (model == null) return;

        try
        {
            await TranscriptionModels.DownloadAsync(modelId, status => SetStatus(status), CancellationToken.None);
            if (string.IsNullOrEmpty(TranscriptionModels.Resolve(_settings.TranscriberModel)))
            {
                _settings.TranscriberModel = modelId;
                _settings.Save();
            }

            foreach (var entry in _nodes.Values.Where(entry =>
                         entry.Modifier?.Processors.OfType<TranscriptionProcessor>().Any() == true &&
                         (string.IsNullOrEmpty(entry.TranscriberModel) ||
                          string.Equals(entry.TranscriberModel, modelId, StringComparison.OrdinalIgnoreCase))))
            {
                ApplyTranscriberModel(entry, modelId);
            }

            PublishSettings();
            PublishGraph();
            SetStatus($"{model.Name} installed.");
        }
        catch (Exception ex)
        {
            SetStatus($"{model.Name} download failed: {ex.Message}", isError: true);
            PublishSettings();
        }
    }
}
