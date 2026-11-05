using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private void AddFromMessage(BridgeMessage message)
    {
        string kind = String(message, "kind");
        string id = String(message, "id");
        string title = String(message, "title");
        string linkId = String(message, "linkId");
        double x = Number(message, "x", 60);
        double y = Number(message, "y", 60);
        uint processId = (uint)Number(message, "processId");

        NodeEntry? entry;
        switch (kind)
        {
            case "app":
            {
                var app = _catalog.Apps.FirstOrDefault(item => item.Id == id) ??
                          _catalog.Apps.FirstOrDefault(item => item.ProcessId == processId) ??
                          throw new InvalidOperationException("The application audio session is no longer available. Refresh and try again.");
                entry = RegisterSource(new SourceNode(app.Name, SourceKind.Application, app.ProcessId,
                    processName: app.ProcessName), x, y);
                SetStatus($"Added source: {app.Name}");
                break;
            }
            case "system":
                entry = RegisterSource(new SourceNode("System audio", SourceKind.SystemLoopback), x, y);
                SetStatus("Added system audio.");
                break;
            case "tone":
            {
                var tone = new SourceNode("Test tone", SourceKind.Tone);
                tone.SetTone(1000f, ToneWaveform.Sine, -18f);
                entry = RegisterSource(tone, x, y);
                SetStatus("Added a test tone.");
                break;
            }
            case "tts":
            {
                var speech = new SourceNode("Text to speech", SourceKind.Tts);
                entry = RegisterSource(speech, x, y);
                entry.TtsText = "";
                entry.TtsModel = _settings.TtsModel;
                entry.TtsSpeed = _settings.TtsSpeed;
                entry.TtsStatus = TtsModels.IsInstalled(_settings.TtsModel) ? "Ready" : "No model installed";
                SetStatus("Added text to speech. Type text or choose a text source.");
                break;
            }
            case "network-in":
            {
                var network = new SourceNode("Network input", SourceKind.Network);
                network.SetNetworkPort(NetworkPcm.DefaultPort);
                entry = RegisterSource(network, x, y);
                SetStatus("Added a network input. Set the UDP port to match the sender.");
                break;
            }
            case "network-out":
                entry = RegisterOutput(OutputNode.CreateForNetwork("127.0.0.1", NetworkPcm.DefaultPort), x, y);
                SetStatus("Added a network output. Set the destination IP and port.");
                break;
            case "input": entry = AddDeviceInput(id, x, y); break;
            case "output": entry = AddDeviceOutput(id, x, y); break;
            case "virtual": entry = AddVirtualOutput(id, x, y); break;
            case "effect": entry = AddEffect(id, x, y); break;
            case "chain": entry = AddChain(id, x, y); break;
            case "vst3":
                entry = AddVst3Plugin(id, x, y);
                break;
            case "file-player":
                AddFilePlayer(x, y);
                return;
            case "recorder":
                entry = RegisterOutput(OutputNode.CreateForFile(string.Empty, AudioFileFormat.Wav24), x, y);
                SetStatus("Added a file recorder. Choose a destination to arm it.");
                break;
            default: throw new InvalidOperationException($"Unknown library item '{kind}'.");
        }

        if (entry != null && !string.IsNullOrWhiteSpace(title))
        {
            ApplyRename(entry.Id, title, publish: false);
        }

        if (entry != null && !string.IsNullOrEmpty(linkId) && InsertModifierIntoLink(entry, linkId))
        {
            PublishGraph();
            return;
        }

        if (entry != null)
        {
            PushAddNode(entry);
        }

        PublishGraph();
    }

    private void AddFilePlayer(double x = 80, double y = 120, string? path = null)
    {
        var source = new SourceNode("File player", SourceKind.File);
        var entry = RegisterSource(source, x, y);
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                source.SetFile(path, loop: false);
                entry.Title = Path.GetFileNameWithoutExtension(path);
                source.Title = entry.Title;
                SetStatus($"File loaded: {Path.GetFileName(path)}. Press Start to play.");
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Could not load dropped file '{path}': {ex.Message}");
                SetStatus($"Could not load '{Path.GetFileName(path)}'.", isError: true);
            }
        }

        PushAddNode(entry);
        PublishGraph();
        PublishTelemetry(force: true);
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("Added a file player. Choose a file to load audio.");
        }
    }

    private void ChooseFilePlayerFile(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.File } source)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an audio file",
            Filter = "Audio files|*.wav;*.mp3;*.flac;*.aiff;*.aif;*.m4a;*.wma;*.ogg|All files|*.*",
            CheckFileExists = true,
            FileName = source.FilePath ?? "",
        };
        if (dialog.ShowDialog(_window) != true)
        {
            return;
        }

        string path = dialog.FileName;
        source.SetFile(path, source.FileLoop);
        entry.Title = Path.GetFileNameWithoutExtension(path);
        source.Title = entry.Title;
        PublishGraph();
        PublishTelemetry(force: true);
        SetStatus($"File loaded: {Path.GetFileName(path)}. Press Start to play.");
    }

    private void SetFilePlayerLoop(string id, bool loop)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.File } source)
        {
            return;
        }

        source.SetFileLoop(loop);
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetFilePlayerSpeed(string id, float speed)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.File } source)
        {
            return;
        }
        source.SetFileSpeed(speed);
        PublishGraph();
        PublishTelemetry(force: true);
    }
    
    private void SetTone(string id, float frequency, string waveform, float levelDb)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Source is not { Kind: SourceKind.Tone } source)
        {
            return;
        }

        ToneWaveform wave = Enum.TryParse<ToneWaveform>(waveform, ignoreCase: true, out var parsed)
            ? parsed
            : source.ToneWave;
        float oldFrequency = source.ToneFrequency;
        ToneWaveform oldWave = source.ToneWave;
        float oldLevel = source.ToneLevelDb;
        source.SetTone(frequency, wave, levelDb);
        PushAction("Change test tone",
            undo: () => ApplyTone(id, oldFrequency, oldWave, oldLevel, publish: true),
            redo: () => ApplyTone(id, frequency, wave, levelDb, publish: true),
            coalesceKey: $"tone:{id}");
    }

    private void ApplyTone(string id, float frequency, ToneWaveform waveform, float levelDb, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Source is not { Kind: SourceKind.Tone } source)
        {
            return;
        }

        source.SetTone(frequency, waveform, levelDb);
        if (publish) PublishGraph();
    }

    private void ChooseImpulseResponse(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null ||
            entry.Modifier.Processors.OfType<IImpulseResponseProcessor>().FirstOrDefault() is not { } processor)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an impulse response",
            Filter = "Impulse responses|*.wav;*.aif;*.aiff;*.flac;*.mp3|All files|*.*",
            CheckFileExists = true,
            FileName = processor.ImpulseResponsePath ?? "",
        };
        if (dialog.ShowDialog(_window) != true)
        {
            return;
        }

        try
        {
            processor.LoadImpulseResponse(dialog.FileName);
            SetStatus($"Impulse response loaded: {processor.ImpulseResponseName}.");
        }
        catch (Exception ex)
        {
            ShowError("Could not load the impulse response: " + ex.Message, ex);
        }

        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void ClearImpulseResponse(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null ||
            entry.Modifier.Processors.OfType<IImpulseResponseProcessor>().FirstOrDefault() is not { } processor)
        {
            return;
        }

        processor.ClearImpulseResponse();
        SetStatus("Using the built-in room impulse response.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void ChooseNoiseProfile(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null ||
            entry.Modifier.Processors.OfType<INoiseProfileProcessor>().FirstOrDefault() is not { } processor)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a noise floor recording",
            Filter = "Audio files|*.wav;*.aif;*.aiff;*.flac;*.mp3|All files|*.*",
            CheckFileExists = true,
            FileName = processor.NoiseProfilePath ?? "",
        };
        if (dialog.ShowDialog(_window) != true)
        {
            return;
        }

        try
        {
            processor.LoadNoiseProfile(dialog.FileName);
            _graph.RefreshModifiers();
            SetStatus($"Noise floor loaded: {processor.NoiseProfileName}.");
        }
        catch (Exception ex)
        {
            ShowError("Could not load the noise floor: " + ex.Message, ex);
        }

        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void ClearNoiseProfile(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null ||
            entry.Modifier.Processors.OfType<INoiseProfileProcessor>().FirstOrDefault() is not { } processor)
        {
            return;
        }

        processor.ClearNoiseProfile();
        _graph.RefreshModifiers();
        SetStatus("Noise floor cleared.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetFilePlayerOutput(string id, string outputId)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.File } source)
        {
            return;
        }

        var existing = _links
            .Where(link => link.Kind == "direct" && link.From == entry.Id)
            .ToList();
        foreach (var link in existing)
        {
            _links.Remove(link);
            if (_nodes.TryGetValue(link.To, out var outputEntry) && outputEntry.Output != null)
            {
                _graph.Disconnect(source, outputEntry.Output);
            }
        }

        source.SelectedOutputId = string.IsNullOrWhiteSpace(outputId) ? null : outputId;
        if (!string.IsNullOrWhiteSpace(outputId) &&
            _nodes.TryGetValue(outputId, out var target) && target.Output != null)
        {
            ConnectEntries(entry, target, publish: false);
        }

        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void RestartFilePlayer(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.File } source)
        {
            return;
        }

        if (!_graph.AutoRun)
        {
            SetStatus("Start routing before restarting the file player.");
            return;
        }

        source.RestartFile();
        PublishTelemetry(force: true);
        SetStatus($"{entry.Title}: restarting from the beginning.");
    }

    private void StartFilePlayer(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.File } source)
        {
            return;
        }

        if (!source.HasFile)
        {
            SetStatus("Choose an audio file first.", isError: true);
            return;
        }

        if (!_graph.AutoRun)
        {
            SetStatus("Start routing before playing the file.", isError: true);
            return;
        }

        try
        {
            source.Start();
            PublishGraph();
            PublishTelemetry(force: true);
            SetStatus($"{entry.Title}: playing.");
        }
        catch (Exception ex)
        {
            ShowError($"Could not play '{entry.Title}': {ex.Message}", ex);
        }
    }

    private void StopFilePlayer(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.File } source)
        {
            return;
        }

        source.Stop();
        PublishGraph();
        PublishTelemetry(force: true);
        SetStatus($"{entry.Title}: stopped.");
    }

    private void PauseFilePlayer(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.File } source)
        {
            return;
        }

        source.PauseFile();
        PublishTelemetry(force: true);
        SetStatus($"{entry.Title}: paused.");
    }

    private void ResumeFilePlayer(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.File } source)
        {
            return;
        }

        source.ResumeFile();
        PublishTelemetry(force: true);
        SetStatus($"{entry.Title}: playing.");
    }

    private void SeekFilePlayer(string id, double seconds)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Source is not { Kind: SourceKind.File } source)
        {
            return;
        }

        source.SeekFile(seconds);
        PublishTelemetry(force: true);
        SetStatus($"{entry.Title}: jumped to {TimeSpan.FromSeconds(Math.Max(0, seconds)):mm\\:ss}.");
    }

    /// <summary>Creates the guided first route and returns its ids.</summary>
    private void CreateFirstRoute(BridgeMessage message)
    {
        string sourceKind = String(message, "sourceKind");
        string sourceId = String(message, "sourceId");
        uint processId = (uint)Number(message, "processId");
        string outputKind = String(message, "outputKind");
        string outputId = String(message, "outputId");

        NodeEntry? source = null;
        bool sourceCreated = false;
        switch (sourceKind)
        {
            case "system":
                source = _nodes.Values.FirstOrDefault(entry =>
                    entry.Source is { Kind: SourceKind.SystemLoopback });
                if (source == null)
                {
                    source = RegisterSource(new SourceNode("System audio", SourceKind.SystemLoopback), 80, 160);
                    sourceCreated = true;
                }
                break;
            case "app":
            {
                var app = _catalog.Apps.FirstOrDefault(item => item.Id == sourceId) ??
                          _catalog.Apps.FirstOrDefault(item => item.ProcessId == processId);
                if (app == null)
                {
                    ShowError("That application audio session is no longer available. Refresh and try again.");
                    return;
                }

                source = _nodes.Values.FirstOrDefault(entry =>
                    entry.Source is { Kind: SourceKind.Application } existing &&
                    (existing.ProcessId == app.ProcessId ||
                     (!string.IsNullOrEmpty(app.ProcessName) &&
                      string.Equals(existing.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase))));
                if (source == null)
                {
                    source = RegisterSource(
                        new SourceNode(app.Name, SourceKind.Application, app.ProcessId,
                            processName: app.ProcessName),
                        80, 160);
                    sourceCreated = true;
                }
                break;
            }
            case "input":
                source = _nodes.Values.FirstOrDefault(entry =>
                    entry.Source is { } existing &&
                    string.Equals(existing.DeviceId, sourceId, StringComparison.OrdinalIgnoreCase));
                if (source == null)
                {
                    source = AddDeviceInput(sourceId, 80, 160);
                    sourceCreated = true;
                }
                break;
            default:
                ShowError("Choose a source for the first route.");
                return;
        }

        NodeEntry? output = FindOutputByDevice(outputId);
        bool outputCreated = false;
        try
        {
            if (output == null)
            {
                output = outputKind == "virtual"
                    ? AddVirtualOutput(outputId, 660, 160)
                    : AddDeviceOutput(outputId, 660, 160);
                outputCreated = output != null;
            }
        }
        catch
        {
            if (sourceCreated)
            {
                RemoveEntryInternal(source.Id);
                PublishGraph();
            }

            throw;
        }

        if (output == null)
        {
            if (sourceCreated)
            {
                RemoveEntryInternal(source.Id);
                PublishGraph();
            }

            ShowError("That output is not available. Refresh and try again.");
            return;
        }

        var link = _links.FirstOrDefault(item => item.From == source.Id && item.To == output.Id);
        bool linkCreated = link == null;
        if (link == null)
        {
            link = ConnectEntries(source, output, publish: false);
        }

        if (sourceCreated)
        {
            PushAddNode(source);
        }

        if (outputCreated)
        {
            PushAddNode(output);
        }

        if (linkCreated)
        {
            PushConnect(link);
        }

        _settings.OnboardingCompleted = true;
        _settings.Save();

        // Start even when the restored session was idle.
        StartRouting();

        var warning = BuildGraphWarnings()
            .FirstOrDefault(item => item.SourceId == source.Id && item.OutputId == output.Id);
        Post("first-route", new FirstRouteDto
        {
            SourceId = source.Id,
            SourceTitle = source.Title,
            OutputTitle = output.Title,
            Warning = warning?.Message,
            Severity = warning?.Severity,
        });

        SetStatus(warning == null
            ? $"First route ready: {source.Title} → {output.Title}."
            : $"First route ready, but this setup can double the audio: {source.Title} → {output.Title}.");
    }

    private void CompleteOnboarding()
    {
        if (!_settings.OnboardingCompleted)
        {
            _settings.OnboardingCompleted = true;
            _settings.Save();
        }

        SetStatus("Welcome guide complete. Reopen it any time from Settings.");
    }

    /// <summary>Inserts a modifier into a cable as one undo step.</summary>
    private bool InsertModifierIntoLink(NodeEntry modifier, string linkId)
    {
        if (modifier.Modifier == null) return false;
        var link = _links.FirstOrDefault(item => item.Id == linkId);
        if (link == null ||
            !_nodes.TryGetValue(link.From, out var from) ||
            !_nodes.TryGetValue(link.To, out var to))
        {
            return false;
        }

        bool valid = link.Kind switch
        {
            "direct" => from.Source != null && to.Output != null,
            "source-modifier" => from.Source != null && to.Modifier != null,
            "modifier-output" => from.Modifier != null && to.Output != null,
            "modifier-modifier" => from.Modifier != null && to.Modifier != null,
            _ => false,
        };
        if (!valid) return false;

        bool wasNative = link.Native;
        if (wasNative)
        {
            foreach (var processor in modifier.Modifier.Processors)
            {
                if (processor is not INativeProcessor)
                {
                    string reason = $"'{processor.DisplayName}' cannot run in native passthrough.";
                    SetStatus(reason, isError: true);
                    Post("link-reject", new { id = link.Id, reason });
                    return false;
                }
            }
        }

        var oldLink = CloneLink(link);
        RemoveLinkInternal(link, releaseNative: !wasNative);
        var left = ConnectEntries(from, modifier);
        var right = ConnectEntries(modifier, to);
        if (wasNative)
        {
            left.Native = true;
            right.Native = true;
            if (TryFindNativeOutput(right, out var nativeOutput) && nativeOutput != null)
            {
                RefreshNativeChain(nativeOutput);
            }
        }
        var nodeSnapshot = CaptureNode(modifier);
        var leftLink = CloneLink(left);
        var rightLink = CloneLink(right);

        PushAction("Insert effect into cable",
            undo: () =>
            {
                RemoveEntryInternal(modifier.Id);
                RestoreLink(oldLink);
                PublishGraph();
            },
            redo: () =>
            {
                RemoveLinkInternal(oldLink);
                RestoreNodeSnapshot(nodeSnapshot);
                RestoreLink(leftLink);
                RestoreLink(rightLink);
                PublishGraph();
            });

        SetStatus($"Inserted '{modifier.Title}' into the cable.");
        return true;
    }

    private NodeEntry AddDeviceInput(string id, double x, double y)
    {
        if (AsioCatalog.IsAsioId(id))
        {
            var driver = FindAsioDriver(id);
            return RegisterSource(new SourceNode(
                driver.Name, SourceKind.Asio, deviceId: id,
                asioDriver: driver.Name, asioChannels: new[] { 0, 1 }), x, y);
        }

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(id);
        var entry = RegisterSource(new SourceNode(device.FriendlyName, SourceKind.Device, deviceId: id), x, y);
        SetStatus($"Added input: {device.FriendlyName}");
        return entry;
    }

    private NodeEntry? AddDeviceOutput(string id, double x, double y)
    {
        if (FindOutputByDevice(id) != null)
        {
            SetStatus("That output is already on the canvas.");
            return null;
        }

        OutputNode output;
        if (AsioCatalog.IsAsioId(id))
        {
            output = new OutputNode(FindAsioDriver(id));
        }
        else
        {
            using var enumerator = new MMDeviceEnumerator();
            output = new OutputNode(enumerator.GetDevice(id));
        }

        var entry = RegisterOutput(output, x, y);
        SetStatus($"Added output: {output.Title}");
        return entry;
    }

    private NodeEntry? AddVirtualOutput(string id, double x, double y)
    {
        if (FindOutputByDevice(id) != null)
        {
            SetStatus("That virtual output is already on the canvas.");
            return null;
        }

        var info = VirtualDeviceCatalog.Discover().FirstOrDefault(item =>
                       string.Equals(item.RenderDeviceId, id, StringComparison.OrdinalIgnoreCase)) ??
                   VirtualDeviceCatalog.ResolveRender(id, "Virtual output", null);
        using var enumerator = new MMDeviceEnumerator();
        var output = new OutputNode(enumerator.GetDevice(info.RenderDeviceId));
        var entry = RegisterOutput(output, x, y, info.CaptureFriendlyName);
        SetStatus($"Added {info.RenderFriendlyName}. Select {info.CaptureFriendlyName} as the microphone in other apps.");
        return entry;
    }

    private NodeEntry AddEffect(string typeId, double x, double y)
    {
        var descriptor = AudioProcessorCatalog.Find(typeId) ??
                         throw new InvalidOperationException($"Unknown effect '{typeId}'.");
        var modifier = new ModifierNode(descriptor.DisplayName);
        var processor = descriptor.Create();
        if (processor is SpectrumProcessor spectrum)
        {
            spectrum.SetTapId(SpectrumProcessor.NextTapId());
        }
        else if (processor is SpectrogramProcessor spectrogram)
        {
            spectrogram.SetTapId(SpectrogramProcessor.NextTapId());
        }
        else if (processor is WaveformProcessor waveform)
        {
            waveform.SetTapId(WaveformProcessor.NextTapId());
        }
        else if (processor is DbMeterProcessor dbMeter)
        {
            dbMeter.SetTapId(DbMeterProcessor.NextTapId());
        }
        else if (processor is LoudnessProcessor loudness)
        {
            loudness.SetTapId(LoudnessProcessor.NextTapId());
        }
        else if (processor is NoteDetectorProcessor note)
        {
            note.SetTapId(NoteDetectorProcessor.NextTapId());
        }
        else if (processor is TranscriptionProcessor transcriber)
        {
            transcriber.SetTapId(TranscriptionProcessor.NextTapId());
        }
        else if (processor is TranslatorProcessor translator)
        {
            translator.SetTapId(TranscriptionProcessor.NextTapId());
        }

        modifier.Add(processor);
        var entry = RegisterModifier(
            modifier,
            x,
            y,
            flavor: processor is TranslatorProcessor ? "translator" :
                processor is SpectrumProcessor or SpectrogramProcessor or WaveformProcessor or LoudnessProcessor ? "analysis" : "effect",
            subtitle: processor is TranslatorProcessor ? "Speech translation" :
                processor is SpectrumProcessor ? "FFT spectrum" :
                processor is SpectrogramProcessor ? "FFT history" :
                processor is WaveformProcessor ? "Time domain" :
                processor is VocoderProcessor ? "Key = modulator" :
                processor is LoudnessProcessor ? "LUFS and true peak" : null,
            transcriberModel: _settings.TranscriberModel,
            translatorTarget: processor is TranslatorProcessor ? _settings.TranslatorTarget : null,
            translatorModel: processor is TranslatorProcessor ? _settings.TranslatorModel : null);
        SetStatus($"Added effect: {descriptor.DisplayName}");
        return entry;
    }

    private void SetVst3Editor(string id, bool open)
    {
        if (!_nodes.TryGetValue(id, out var entry) ||
            entry.Modifier?.Processors.OfType<IVst3Processor>().FirstOrDefault() is not { } vst)
        {
            return;
        }

        try
        {
            if (open)
            {
                if (!vst.OpenEditor(entry.Title))
                {
                    SetStatus("This VST3 has no editor window.", isError: true);
                }
            }
            else
            {
                vst.CloseEditor();
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not open the VST3 editor: " + ex.Message, ex);
        }

        PublishGraph();
        PublishTelemetry(force: true);
    }

    private NodeEntry AddVst3Plugin(string identifier, double x, double y)
    {
        var descriptor = FindVst3Descriptor(identifier);
        string name = string.IsNullOrWhiteSpace(descriptor?.Name) ? "VST3 plugin" : descriptor!.Name;
        var processor = Vst3ProcessorFactory.Create(identifier, start: false);
        var modifier = new ModifierNode(name);
        modifier.Add(processor);
        var entry = RegisterModifier(
            modifier,
            x,
            y,
            flavor: "vst3",
            subtitle: "VST3 effect");
        entry.Title = name;
        SetStatus($"Added VST3: {name}.");
        return entry;
    }

    private NodeEntry AddChain(string name, double x, double y)
    {
        var chain = SubgraphLibrary.Chains.FirstOrDefault(item =>
                        string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) ??
                    throw new InvalidOperationException($"Chain '{name}' is no longer in the library.");
        var modifier = new ModifierNode(chain.Name);
        modifier.Add(chain.CreateProcessor());
        var entry = RegisterModifier(modifier, x, y, flavor: "chain", subtitle: chain.Description);
        SetStatus($"Added chain: {chain.Name}");
        return entry;
    }

    private AsioDriverInfo FindAsioDriver(string id)
    {
        return _asioDrivers.FirstOrDefault(driver => string.Equals(driver.Id, id, StringComparison.OrdinalIgnoreCase)) ??
               AsioCatalog.Discover().FirstOrDefault(driver => string.Equals(driver.Id, id, StringComparison.OrdinalIgnoreCase)) ??
               throw new InvalidOperationException($"ASIO driver '{AsioCatalog.NameFromId(id)}' is not available.");
    }

    private NodeEntry? FindOutputByDevice(string id)
    {
        return _nodes.Values.FirstOrDefault(entry => entry.Output != null &&
            !entry.Output.IsFileRecorder && string.Equals(entry.Output.DeviceId, id, StringComparison.OrdinalIgnoreCase));
    }

    private NodeEntry RegisterSource(SourceNode source, double x, double y, string? id = null, string? controlId = null)
    {
        _graph.AddSource(source);
        var entry = new NodeEntry
        {
            Id = id ?? source.Id,
            Kind = "source",
            Flavor = source.Kind switch
            {
                SourceKind.Application => "app",
                SourceKind.Device => "mic",
                SourceKind.Asio => "asio",
                SourceKind.File => "file",
                SourceKind.Tone => "tone",
                SourceKind.Tts => "tts",
                SourceKind.Network => "network",
                _ => "system",
            },
            Title = source.Title,
            Subtitle = source.Kind switch
            {
                SourceKind.Application => SourceNode.SupportsApplicationCapture ? "Application" : "Capture unavailable",
                SourceKind.Device => "Input device",
                SourceKind.Asio => "ASIO input",
                SourceKind.File => "Audio file",
                SourceKind.Tone => "Test tone",
                SourceKind.Tts => "Text to speech",
                SourceKind.Network => "Network input",
                _ => "System audio",
            },
            Accent = source.Kind switch
            {
                SourceKind.Application => "#5B8DEF",
                SourceKind.SystemLoopback => "#65D6D0",
                SourceKind.Asio => "#F0DC5A",
                SourceKind.File => "#F08CB8",
                SourceKind.Tone => "#F0DC5A",
                SourceKind.Tts => "#C7A6FF",
                SourceKind.Network => "#4FD1C5",
                _ => IsVirtualCapture(source.Title) ? "#7D5BD6" : "#F0DC5A",
            },
            X = x,
            Y = y,
            Source = source,
        };
        _nodes.Add(entry.Id, entry);
        entry.ControlId = MakeUniqueControlId(entry, controlId);
        UpdateSourceEndpoint(entry);
        source.Faulted += (node, reason) => _window.Dispatcher.BeginInvoke(new Action(() =>
            OnNodeFault(entry.Id, $"Capture '{node.Title}' stopped: {reason}")));
        return entry;
    }

    private NodeEntry RegisterOutput(OutputNode output, double x, double y, string? micHint = null, string? id = null, string? controlId = null)
    {
        // A file recorder with no destination cannot carry audio yet. Keep it
        // out of the render graph until it is connected or actually recording.
        if (!output.IsFileRecorder)
        {
            _graph.AddOutputNode(output);
        }
        string flavor = output.IsFileRecorder ? "recorder" : output.IsNetworkOutput ? "network" : micHint != null ? "virtual" : AsioCatalog.IsAsioId(output.DeviceId) ? "asio" : "output";
        var entry = new NodeEntry
        {
            Id = id ?? output.Id,
            Kind = "output",
            Flavor = flavor,
            Title = output.Title,
            Subtitle = output.IsFileRecorder ? "File output" : output.IsNetworkOutput ? "Network output" : micHint != null ? "Virtual output" : AsioCatalog.IsAsioId(output.DeviceId) ? "ASIO output" : "Output device",
            Accent = output.IsFileRecorder ? "#FF8B72" : output.IsNetworkOutput ? "#4FD1C5" : micHint != null ? "#D9A8FF" : "#43E39A",
            X = x,
            Y = y,
            MicHint = micHint,
            Output = output,
        };
        _nodes.Add(entry.Id, entry);
        entry.ControlId = MakeUniqueControlId(entry, controlId);
        output.Faulted += (node, reason) => _window.Dispatcher.BeginInvoke(new Action(() =>
            OnNodeFault(entry.Id, $"Output '{node.Title}' stopped: {reason}")));
        return entry;
    }

    private NodeEntry RegisterModifier(
        ModifierNode modifier,
        double x,
        double y,
        string flavor = "effect",
        string? subtitle = null,
        string? id = null,
        string? transcriberModel = null,
        string? translatorTarget = null,
        string? translatorModel = null,
        string? controlId = null)
    {
        EnsureUniqueTapIds(modifier);
        _graph.AddModifier(modifier);
        foreach (var spectrum in modifier.Processors.OfType<SpectrumProcessor>())
        {
            spectrum.SetTapId(spectrum.TapId > 0 ? spectrum.TapId : SpectrumProcessor.NextTapId());
        }

        foreach (var spectrogram in modifier.Processors.OfType<SpectrogramProcessor>())
        {
            spectrogram.SetTapId(spectrogram.TapId > 0 ? spectrogram.TapId : SpectrogramProcessor.NextTapId());
        }

        foreach (var waveform in modifier.Processors.OfType<WaveformProcessor>())
        {
            waveform.SetTapId(waveform.TapId > 0 ? waveform.TapId : WaveformProcessor.NextTapId());
        }

        foreach (var dbMeter in modifier.Processors.OfType<DbMeterProcessor>())
        {
            dbMeter.SetTapId(dbMeter.TapId > 0 ? dbMeter.TapId : DbMeterProcessor.NextTapId());
        }

        foreach (var loudness in modifier.Processors.OfType<LoudnessProcessor>())
        {
            loudness.SetTapId(loudness.TapId > 0 ? loudness.TapId : LoudnessProcessor.NextTapId());
        }

        foreach (var note in modifier.Processors.OfType<NoteDetectorProcessor>())
        {
            note.SetTapId(note.TapId > 0 ? note.TapId : NoteDetectorProcessor.NextTapId());
        }

        bool hasTranscriber = modifier.Processors.OfType<TranscriptionProcessor>().Any();
        bool hasTranslator = modifier.Processors.OfType<TranslatorProcessor>().Any();
        string? resolvedModel = hasTranscriber
            ? TranscriptionModels.Resolve(transcriberModel ?? _settings.TranscriberModel)
            : null;
        string? resolvedTranslatorSpeechModel = hasTranslator
            ? TranscriptionModels.ResolveMultilingual(transcriberModel ?? _settings.TranscriberModel)
            : null;
        string? resolvedTarget = hasTranslator
            ? TranslationLanguages.Normalize(translatorTarget ?? _settings.TranslatorTarget)
            : null;
        string? resolvedTranslationModel = hasTranslator && TranslationLanguages.NeedsModel(resolvedTarget)
            ? TranslationModels.Resolve(translatorModel ?? _settings.TranslatorModel)
            : null;

        foreach (var transcriber in modifier.Processors.OfType<TranscriptionProcessor>())
        {
            TranscriptionProcessor.EnsureTap(transcriber.TapId);
            if (!string.IsNullOrEmpty(resolvedModel))
            {
                TranscriptionRegistry.Ensure(transcriber.TapId, resolvedModel);
            }
        }

        foreach (var translator in modifier.Processors.OfType<TranslatorProcessor>())
        {
            TranscriptionProcessor.EnsureTap(translator.TapId);
            if (!string.IsNullOrEmpty(resolvedTranslatorSpeechModel))
            {
                TranslationRegistry.Ensure(
                    translator.TapId,
                    resolvedTranslatorSpeechModel,
                    resolvedTranslationModel ?? "",
                    resolvedTarget ?? TranslationLanguages.DefaultCode,
                    _settings.TranslationProvider);
            }
        }

        var entry = new NodeEntry
        {
            Id = id ?? modifier.Id,
            Kind = "modifier",
            Flavor = flavor,
            Title = modifier.Title,
            Subtitle = subtitle ?? "Processing",
            Accent = flavor == "chain" ? "#B18CFF" : flavor == "translator" ? "#73C8E8" :
                flavor == "vst3" ? "#A3E635" : flavor == "analysis" ? "#65D6D0" : "#E5AD51",
            X = x,
            Y = y,
            TranscriberModel = hasTranslator ? resolvedTranslatorSpeechModel : resolvedModel,
            TranslatorTarget = resolvedTarget,
            TranslatorModel = resolvedTranslationModel,
            Modifier = modifier,
        };
        _nodes.Add(entry.Id, entry);
        entry.ControlId = MakeUniqueControlId(entry, controlId);

        if (modifier.Processors.OfType<IVst3Processor>().FirstOrDefault() is { } vst)
        {
            vst.Changed += () => _window.Dispatcher.BeginInvoke(new Action(() => OnVst3Changed(entry.Id)));
            vst.Start();
        }

        return entry;
    }

    private void OnVst3Changed(string id)
    {
        if (_disposed || !_nodes.TryGetValue(id, out var entry) || entry.Modifier == null)
        {
            return;
        }

        var vst = entry.Modifier.Processors.OfType<IVst3Processor>().FirstOrDefault();
        if (vst == null)
        {
            return;
        }

        if (entry.Title == "VST3 plugin" && !string.IsNullOrWhiteSpace(vst.PluginName))
        {
            entry.Title = vst.PluginName;
        }

        if (entry.Flavor == "vst3")
        {
            entry.Subtitle = string.IsNullOrWhiteSpace(vst.Vendor) ? "VST3 effect" : $"{vst.Vendor} · VST3";
        }

        if (!string.IsNullOrWhiteSpace(vst.Error))
        {
            SetStatus($"{entry.Title}: {vst.Error}", isError: true);
        }

        // The plugin may have loaded after the chain was built; its latency is
        // only known now. Polling fires while it loads, so rebuild only when the
        // reported latency actually changes instead of on every status tick.
        int latency = vst.LatencySamples;
        bool latencyChanged = latency != entry.Vst3Latency;
        entry.Vst3Latency = latency;
        entry.Vst3Ready = vst.IsReady;
        if (latencyChanged)
        {
            foreach (var output in _graph.Outputs)
            {
                _graph.RebuildRouteProcessors(output);
            }
        }

        PublishGraph();
        PublishTelemetry(force: true);
    }

    /// <summary>Gives duplicated nodes their own tap.</summary>
    private void EnsureUniqueTapIds(ModifierNode modifier)
    {
        var used = new HashSet<int>();
        foreach (var entry in _nodes.Values)
        {
            if (entry.Modifier == null)
            {
                continue;
            }

            foreach (var spectrum in entry.Modifier.Processors.OfType<SpectrumProcessor>())
            {
                used.Add(spectrum.TapId);
            }

            foreach (var spectrogram in entry.Modifier.Processors.OfType<SpectrogramProcessor>())
            {
                used.Add(spectrogram.TapId);
            }

            foreach (var waveform in entry.Modifier.Processors.OfType<WaveformProcessor>())
            {
                used.Add(waveform.TapId);
            }

            foreach (var transcriber in entry.Modifier.Processors.OfType<TranscriptionProcessor>())
            {
                used.Add(transcriber.TapId);
            }

            foreach (var translator in entry.Modifier.Processors.OfType<TranslatorProcessor>())
            {
                used.Add(translator.TapId);
            }

            foreach (var dbMeter in entry.Modifier.Processors.OfType<DbMeterProcessor>())
            {
                used.Add(dbMeter.TapId);
            }

            foreach (var loudness in entry.Modifier.Processors.OfType<LoudnessProcessor>())
            {
                used.Add(loudness.TapId);
            }

            foreach (var note in entry.Modifier.Processors.OfType<NoteDetectorProcessor>())
            {
                used.Add(note.TapId);
            }
        }

        foreach (var spectrum in modifier.Processors.OfType<SpectrumProcessor>())
        {
            if (spectrum.TapId <= 0 || used.Contains(spectrum.TapId))
            {
                spectrum.SetTapId(SpectrumProcessor.NextTapId());
            }

            used.Add(spectrum.TapId);
        }

        foreach (var spectrogram in modifier.Processors.OfType<SpectrogramProcessor>())
        {
            if (spectrogram.TapId <= 0 || used.Contains(spectrogram.TapId))
            {
                spectrogram.SetTapId(SpectrogramProcessor.NextTapId());
            }

            used.Add(spectrogram.TapId);
        }

        foreach (var waveform in modifier.Processors.OfType<WaveformProcessor>())
        {
            if (waveform.TapId <= 0 || used.Contains(waveform.TapId))
            {
                waveform.SetTapId(WaveformProcessor.NextTapId());
            }

            used.Add(waveform.TapId);
        }

        foreach (var transcriber in modifier.Processors.OfType<TranscriptionProcessor>())
        {
            if (transcriber.TapId <= 0 || used.Contains(transcriber.TapId))
            {
                transcriber.SetTapId(TranscriptionProcessor.NextTapId());
            }

            used.Add(transcriber.TapId);
        }

        foreach (var translator in modifier.Processors.OfType<TranslatorProcessor>())
        {
            if (translator.TapId <= 0 || used.Contains(translator.TapId))
            {
                translator.SetTapId(TranscriptionProcessor.NextTapId());
            }

            used.Add(translator.TapId);
        }

        foreach (var dbMeter in modifier.Processors.OfType<DbMeterProcessor>())
        {
            if (dbMeter.TapId <= 0 || used.Contains(dbMeter.TapId))
            {
                dbMeter.SetTapId(DbMeterProcessor.NextTapId());
            }

            used.Add(dbMeter.TapId);
        }

        foreach (var loudness in modifier.Processors.OfType<LoudnessProcessor>())
        {
            if (loudness.TapId <= 0 || used.Contains(loudness.TapId))
            {
                loudness.SetTapId(LoudnessProcessor.NextTapId());
            }

            used.Add(loudness.TapId);
        }

        foreach (var note in modifier.Processors.OfType<NoteDetectorProcessor>())
        {
            if (note.TapId <= 0 || used.Contains(note.TapId))
            {
                note.SetTapId(NoteDetectorProcessor.NextTapId());
            }

            used.Add(note.TapId);
        }

        _graph.RefreshModifiers();
    }

    private void OnNodeFault(string id, string reason)
    {
        if (!_nodes.TryGetValue(id, out var entry) || _disposed)
        {
            return;
        }

        entry.Subtitle = entry.Kind == "source" ? "Capture stopped" : "Disconnected";
        ShowError(reason);
        PublishGraph();
        if (_running && !_graph.Outputs.Any(output => output.IsRunning))
        {
            StopRouting();
        }
    }

    private NodeDto CreateNodeDto(NodeEntry entry)
    {
        var dto = new NodeDto
        {
            Id = entry.Id,
            ControlId = entry.ControlId,
            Kind = entry.Kind,
            Flavor = entry.Flavor,
            Title = entry.Title,
            Subtitle = entry.Subtitle,
            Accent = string.IsNullOrWhiteSpace(entry.Color) ? entry.Accent : entry.Color,
            Color = entry.Color,
            X = entry.X,
            Y = entry.Y,
            MicHint = entry.MicHint,
        };

        if (entry.Source != null)
        {
            dto.Volume = entry.Source.Gain;
            dto.Muted = entry.Source.Muted;
            dto.Solo = entry.Source.Solo;
            dto.DeviceId = entry.Source.DeviceId;
            dto.SampleRate = entry.Source.SampleRate > 0 ? entry.Source.SampleRate : 48000;
            dto.BitsPerSample = entry.Source.SampleRate > 0 ? entry.Source.BitsPerSample : 32;
            dto.IsFilePlayer = entry.Source.Kind == SourceKind.File;
            dto.FilePath = entry.Source.FilePath;
            dto.FileOutputId = entry.Source.SelectedOutputId;
            dto.FileLoop = entry.Source.FileLoop;
            dto.FileSpeed = entry.Source.FileSpeed;
            dto.IsToneGenerator = entry.Source.Kind == SourceKind.Tone;
            dto.ToneFrequency = entry.Source.ToneFrequency;
            dto.ToneWaveform = entry.Source.ToneWave.ToString();
            dto.ToneLevelDb = entry.Source.ToneLevelDb;
            dto.IsTts = entry.Source.Kind == SourceKind.Tts;
            dto.IsNetworkInput = entry.Source.Kind == SourceKind.Network;
            dto.NetworkLocalIp = entry.Source.Kind == SourceKind.Network ? NetworkPcm.GetLocalAddress() : null;
            dto.NetworkPort = entry.Source.NetworkPort;
            dto.NetworkError = entry.Source.NetworkError;
            dto.NetworkBufferedMs = entry.Source.NetworkBufferedMs;
            dto.TtsText = entry.TtsText;
            dto.TtsSourceNodeId = entry.TtsSourceNodeId;
            dto.TtsModel = entry.TtsModel ?? _settings.TtsModel;
            dto.TtsSpeed = entry.TtsSpeed;
            dto.TtsVoice = entry.TtsVoice;
            dto.TtsUsesGpu = false;
            dto.TtsVoices = TtsModels.Find(dto.TtsModel)?.Voices.ToList() ?? new();
            dto.TtsStatus = entry.TtsStatus;
            dto.TtsModelInstalled = TtsModels.IsInstalled(dto.TtsModel);
            dto.TtsSources = entry.Source.Kind == SourceKind.Tts ? BuildTtsSources(entry.Id) : new();
        }
        else if (entry.Output != null)
        {
            dto.Volume = entry.Output.Volume;
            dto.Muted = entry.Output.Muted;
            dto.Solo = entry.Output.Solo;
            dto.DeviceId = entry.Output.DeviceId;
            dto.SampleRate = entry.Output.EndpointSampleRate;
            dto.IsRecorder = entry.Output.IsFileRecorder;
            dto.IsNetworkOutput = entry.Output.IsNetworkOutput;
            dto.NetworkLocalIp = entry.Output.IsNetworkOutput ? NetworkPcm.GetLocalAddress() : null;
            dto.NetworkHost = entry.Output.NetworkHost;
            dto.NetworkPort = entry.Output.NetworkPort;
            dto.NetworkError = entry.Output.NetworkError;
            dto.RecordingPath = entry.Output.RecordingPath;
            dto.RecordingFormat = entry.Output.RecordingFormat.ToString();
            dto.RecorderSampleRate = entry.Output.RecorderSampleRate;
            dto.RecorderMode = entry.Output.RecorderMode.ToString();
            dto.RecorderAutoSave = entry.Output.AutoSave;
            dto.RecorderSilenceStopSeconds = entry.Output.RecorderSilenceStopSeconds;
            dto.RecorderStartThresholdDb = entry.Output.RecorderStartThresholdDb;
        }
        else if (entry.Modifier != null)
        {
            dto.Enabled = entry.Modifier.Enabled;
            dto.IsParallel = entry.Modifier.Parallel;
            dto.Send = entry.Modifier.Send;
            dto.IsSpectrum = entry.Modifier.Processors.OfType<SpectrumProcessor>().Any();
            dto.IsSpectrogram = entry.Modifier.Processors.OfType<SpectrogramProcessor>().Any();
            dto.IsWaveform = entry.Modifier.Processors.OfType<WaveformProcessor>().Any();
            dto.IsGraphicEq = entry.Modifier.Processors.Count == 1 &&
                entry.Modifier.Processors[0] is GraphicEqualizerProcessor;
            dto.IsParametricEq = entry.Modifier.Processors.OfType<ParametricEqProcessor>().Any();
            if (dto.IsParametricEq)
            {
                dto.EqBandTypes = ParametricEqProcessor.BandTypes.ToList();
            }

            if (entry.Modifier.Processors.OfType<IImpulseResponseProcessor>().FirstOrDefault() is { } impulse)
            {
                dto.IsConvolutionReverb = true;
                dto.IrPath = impulse.ImpulseResponsePath;
                dto.IrName = impulse.ImpulseResponseName;
                dto.IrError = impulse.ImpulseResponseError;
            }

            if (entry.Modifier.Processors.OfType<INoiseProfileProcessor>().FirstOrDefault() is { } noise)
            {
                dto.IsNoiseSuppression = true;
                dto.NoiseProfilePath = noise.NoiseProfilePath;
                dto.NoiseProfileName = noise.NoiseProfileName;
                dto.NoiseProfileError = noise.NoiseProfileError;
            }

            dto.IsDbMeter = entry.Modifier.Processors.OfType<DbMeterProcessor>().Any();
            dto.IsLoudness = entry.Modifier.Processors.OfType<LoudnessProcessor>().Any();
            dto.IsNoteDetector = entry.Modifier.Processors.OfType<NoteDetectorProcessor>().Any();
            dto.IsTranscriber = entry.Modifier.Processors.OfType<TranscriptionProcessor>().Any();
            dto.IsTranslator = entry.Modifier.Processors.OfType<TranslatorProcessor>().Any();
            dto.SupportsSidechain = entry.Modifier.Processors.Any(SidechainRouting.Supports);
            dto.TranscriberModel = entry.TranscriberModel;
            dto.TranslatorTarget = entry.TranslatorTarget;
            dto.TranslatorModel = entry.TranslatorModel;
            dto.TranslatorProvider = _settings.TranslationProvider;
            if (entry.Modifier.Processors.OfType<IVst3Processor>().FirstOrDefault() is { } vst)
            {
                dto.IsVst3 = true;
                dto.Vst3Identifier = vst.Identifier;
                dto.Vst3Status = vst.Status;
                dto.Vst3Vendor = vst.Vendor;
                dto.Vst3Version = vst.Version;
                dto.Vst3Error = vst.Error;
                dto.Vst3HasEditor = vst.HasEditor;
                dto.Vst3EditorOpen = vst.IsEditorOpen;
                dto.Vst3LatencyMs = (int)Math.Round(vst.LatencySamples * 1000.0 / 48000.0);
            }
            var chain = entry.Modifier.Processors.OfType<ChainProcessor>().FirstOrDefault();
            if (chain != null)
            {
                dto.ChainName = chain.DisplayName;
                dto.ChainElements = chain.Processors.Select(processor => processor.DisplayName).ToList();
            }

            for (int processorIndex = 0; processorIndex < entry.Modifier.Processors.Count; processorIndex++)
            {
                if (entry.Modifier.Processors[processorIndex] is not IParameterizedProcessor parameterized)
                {
                    continue;
                }

                for (int parameterIndex = 0; parameterIndex < parameterized.Parameters.Count; parameterIndex++)
                {
                    var parameter = parameterized.Parameters[parameterIndex];
                    if (parameter.Name is SpectrumProcessor.TapParameterName or SpectrogramProcessor.TapParameterName or TranscriptionProcessor.TapParameterName or DbMeterProcessor.TapParameterName) continue;
                    if (parameterized is DbMeterProcessor && parameter.Name == "Orientation") continue;
                    dto.Parameters.Add(new ParameterDto
                    {
                        ProcessorIndex = processorIndex,
                        ParameterIndex = parameterIndex,
                        Name = parameter.Name,
                        Min = parameter.Min,
                        Max = parameter.Max,
                        Value = parameter.Get(),
                        Unit = parameter.Unit,
                        Logarithmic = parameter.Logarithmic,
                        IsToggle = parameter.IsToggle,
                        IsInteger = parameter.IsInteger,
                        IsChoice = parameter.IsChoice,
                        Options = parameter.Options,
                        MidiBinding = MidiBindingLabel(entry.Id, processorIndex, parameterIndex),
                        MidiLearning = IsMidiLearning(entry.Id, processorIndex, parameterIndex),
                    });
                }
            }
        }

        return dto;
    }

    private void MoveNode(string id, double x, double y)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        double oldX = entry.X;
        double oldY = entry.Y;
        entry.X = x;
        entry.Y = y;
        double newX = entry.X;
        double newY = entry.Y;
        PushAction("Move node",
            undo: () => SetNodePosition(id, oldX, oldY, publish: true),
            redo: () => SetNodePosition(id, newX, newY, publish: true),
            coalesceKey: $"move:{id}");
        PublishGraph();
    }

    private void SetNodePosition(string id, double x, double y, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        entry.X = x;
        entry.Y = y;
        if (publish) PublishGraph();
    }

    private void SetNodeGain(string id, float value)
    {
        value = Math.Clamp(value, 0, 1.5f);
        if (!_nodes.TryGetValue(id, out var entry)) return;
        float old = entry.Source?.Gain ?? entry.Output?.Volume ?? 1;
        if (entry.Source != null) entry.Source.Gain = value;
        if (entry.Output != null) entry.Output.Volume = value;
        PushAction("Change volume",
            undo: () => ApplyNodeGain(id, old, publish: true),
            redo: () => ApplyNodeGain(id, value, publish: true),
            coalesceKey: $"gain:{id}",
            structural: false);
        PublishNodeState(id);
    }

    private void ApplyNodeGain(string id, float value, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        if (entry.Source != null) entry.Source.Gain = value;
        if (entry.Output != null) entry.Output.Volume = value;
        if (publish) PublishNodeState(id);
    }

    private void SetNodeMute(string id, bool muted)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        bool old = entry.Source?.Muted ?? entry.Output?.Muted ?? false;
        ApplyNodeMute(id, muted, publish: true);
        PushAction("Mute node",
            undo: () => ApplyNodeMute(id, old, publish: true),
            redo: () => ApplyNodeMute(id, muted, publish: true),
            structural: false);
    }

    private void ApplyNodeMute(string id, bool muted, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        if (entry.Source != null)
        {
            entry.Source.Muted = muted;
            if (entry.Source.Kind == SourceKind.Network)
            {
                try
                {
                    if (muted) entry.Source.Stop();
                    else entry.Source.Start();
                }
                catch (Exception ex)
                {
                    SetStatus($"Could not start network input: {ex.Message}", isError: true);
                }
            }
        }

        if (entry.Output != null)
        {
            entry.Output.Muted = muted;
            if (entry.Output.IsNetworkOutput)
            {
                try
                {
                    if (muted) entry.Output.Stop();
                    else entry.Output.Start();
                }
                catch (Exception ex)
                {
                    SetStatus($"Could not start network output: {ex.Message}", isError: true);
                }
            }
        }

        if (publish) PublishNodeState(id);
    }

    private void SetNodeSolo(string id, bool solo)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        if (entry.Source != null) entry.Source.Solo = solo;
        if (entry.Output != null) entry.Output.Solo = solo;
        PublishNodeState(id);
    }

    private void SetNodeColor(string id, string color)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        string? normalized = NormalizeNodeColor(color);
        if (string.IsNullOrWhiteSpace(color))
        {
            entry.Color = null;
        }
        else if (normalized == null)
        {
            return;
        }
        else
        {
            entry.Color = normalized;
        }

        PublishNodeState(id);
    }

    private static string? NormalizeNodeColor(string? color) =>
        !string.IsNullOrWhiteSpace(color) && color.Length == 7 && color[0] == '#' &&
        color.Skip(1).All(Uri.IsHexDigit)
            ? color.ToUpperInvariant()
            : null;

    private void SetParameter(BridgeMessage message)
    {
        string id = String(message, "id");
        int processorIndex = (int)Number(message, "processor");
        int parameterIndex = (int)Number(message, "parameter");
        float value = (float)Number(message, "value");
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null ||
            processorIndex < 0 || processorIndex >= entry.Modifier.Processors.Count ||
            entry.Modifier.Processors[processorIndex] is not IParameterizedProcessor parameterized ||
            parameterIndex < 0 || parameterIndex >= parameterized.Parameters.Count)
        {
            return;
        }

        if (!float.IsFinite(value))
        {
            return;
        }

        var parameter = parameterized.Parameters[parameterIndex];
        value = Math.Clamp(value, parameter.Min, parameter.Max);
        if (parameter.IsInteger) value = MathF.Round(value);
        float old = parameter.Get();
        parameter.Set(value);
        string key = $"param:{id}:{processorIndex}:{parameterIndex}";
        PushAction($"Change {parameter.Name}",
            undo: () => ApplyParameter(id, processorIndex, parameterIndex, old, publish: true),
            redo: () => ApplyParameter(id, processorIndex, parameterIndex, value, publish: true),
            coalesceKey: key);
        PublishParameterChange(id, processorIndex, parameterIndex);
    }

    private void PublishParameterChange(string id, int processorIndex, int parameterIndex)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null ||
            processorIndex < 0 || processorIndex >= entry.Modifier.Processors.Count ||
            entry.Modifier.Processors[processorIndex] is not IParameterizedProcessor parameterized ||
            parameterIndex < 0 || parameterIndex >= parameterized.Parameters.Count)
        {
            return;
        }

        Post("param", new
        {
            id,
            processor = processorIndex,
            parameter = parameterIndex,
            value = parameterized.Parameters[parameterIndex].Get(),
        });
    }

    private void ApplyParameter(string id, int processorIndex, int parameterIndex, float value, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null ||
            processorIndex < 0 || processorIndex >= entry.Modifier.Processors.Count ||
            entry.Modifier.Processors[processorIndex] is not IParameterizedProcessor parameterized ||
            parameterIndex < 0 || parameterIndex >= parameterized.Parameters.Count)
        {
            return;
        }

        if (!float.IsFinite(value))
        {
            return;
        }

        var parameter = parameterized.Parameters[parameterIndex];
        value = Math.Clamp(value, parameter.Min, parameter.Max);
        if (parameter.IsInteger) value = MathF.Round(value);
        parameter.Set(value);
        if (publish) PublishGraph();
    }

    private void SetEffectEnabled(string id, bool enabled)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        bool old = entry.Modifier.Enabled;
        ApplyEffectEnabled(id, enabled, publish: true);
        PushAction(enabled ? "Enable effect" : "Bypass effect",
            undo: () => ApplyEffectEnabled(id, old, publish: true),
            redo: () => ApplyEffectEnabled(id, enabled, publish: true),
            structural: false);
        SetStatus($"{entry.Title}: {(enabled ? "enabled" : "bypassed")}.");
    }

    private void ApplyEffectEnabled(string id, bool enabled, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        entry.Modifier.Enabled = enabled;
        foreach (var output in _graph.Outputs)
        {
            // The graph renderer reads Modifier.Enabled every block and
            // crossfades wet/dry in place. Recompiling would reset that fade
            // and make the bypass click. Only non-graph outputs need their
            // cloned chains rebuilt.
            if (!output.UsesGraphEngine)
            {
                _graph.RebuildRouteProcessors(output);
            }
        }

        if (publish) PublishNodeState(id);
    }

    private void SetModifierParallel(string id, bool parallel)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        bool old = entry.Modifier.Parallel;
        ApplyModifierParallel(id, parallel, publish: true);
        PushAction(parallel ? "Enable parallel send" : "Disable parallel send",
            undo: () => ApplyModifierParallel(id, old, publish: true),
            redo: () => ApplyModifierParallel(id, parallel, publish: true),
            structural: false);
    }

    private void ApplyModifierParallel(string id, bool parallel, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        entry.Modifier.Parallel = parallel;
        _graph.RefreshModifiers();
        if (publish) PublishNodeState(id);
    }

    private void SetModifierSend(string id, float value)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        float old = entry.Modifier.Send;
        float send = Math.Clamp(value, 0f, 1f);
        ApplyModifierSend(id, send, publish: true);
        PushAction("Change send amount",
            undo: () => ApplyModifierSend(id, old, publish: true),
            redo: () => ApplyModifierSend(id, send, publish: true),
            coalesceKey: $"send:{id}",
            structural: false);
    }

    private void ApplyModifierSend(string id, float value, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        float send = Math.Clamp(value, 0f, 1f);
        entry.Modifier.Send = send;
        if (entry.Modifier.ParallelChain != null)
        {
            entry.Modifier.ParallelChain.Send = send;
        }
        else if (entry.Modifier.Parallel)
        {
            _graph.RefreshModifiers();
        }

        if (publish) PublishNodeState(id);
    }

    private void ResetLoudness(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null) return;
        foreach (var loudness in entry.Modifier.Processors.OfType<LoudnessProcessor>())
        {
            LoudnessProcessor.ResetTap(loudness.TapId);
        }

        PublishTelemetry(force: true);
    }

    private void SetDbMeterOrientation(string id, bool vertical)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Modifier == null)
        {
            return;
        }

        var meter = entry.Modifier.Processors.OfType<DbMeterProcessor>().FirstOrDefault();
        if (meter == null)
        {
            return;
        }

        meter.IsVertical = vertical;
        SetStatus($"{entry.Title}: {(vertical ? "vertical" : "horizontal")} meter.");
        PublishTelemetry(force: true);
    }

    private void RenameNode(string id, string title)
    {
        if (!_nodes.TryGetValue(id, out var entry) || string.IsNullOrWhiteSpace(title)) return;
        string old = entry.Title;
        title = title.Trim();
        title = title.Length > 80 ? title[..80] : title;
        ApplyRename(id, title, publish: true);
        PushAction("Rename node",
            undo: () => ApplyRename(id, old, publish: true),
            redo: () => ApplyRename(id, title, publish: true));
        SetStatus($"Renamed node to {title}.");
    }

    private void ApplyRename(string id, string title, bool publish)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        entry.Title = title;
        if (entry.Source != null) entry.Source.Title = title;
        if (entry.Output != null) entry.Output.Title = title;
        if (entry.Modifier != null) entry.Modifier.Title = title;
        if (publish) PublishGraph();
    }

    private void ConnectNodes(string fromId, string toId, string kind = "", float gain = 1, bool enabled = true)
    {
        if (!_nodes.TryGetValue(fromId, out var from) || !_nodes.TryGetValue(toId, out var to) || fromId == toId)
        {
            SetStatus("Choose a source or modifier OUT and a modifier or output IN.", isError: true);
            return;
        }

        bool sidechain = string.Equals(kind, "sidechain", StringComparison.OrdinalIgnoreCase);
        if (sidechain &&
            ((from.Source == null && from.Modifier == null) || to.Modifier == null ||
             !to.Modifier.Processors.Any(SidechainRouting.Supports)))
        {
            SetStatus("Connect a source or effect output to a modifier that supports sidechain input.", isError: true);
            return;
        }

        if (_links.Any(link => link.From == fromId && link.To == toId &&
                               (sidechain
                                   ? link.Kind == "sidechain"
                                   : link.Kind != "sidechain")))
        {
            SetStatus("Those nodes are already connected.", isError: true);
            return;
        }

        var link = ConnectEntries(from, to, gain: gain, publish: false, kind: sidechain ? "sidechain" : null);
        link.Enabled = enabled;
        ApplyLinkRouting(link, publish: false);
        PushConnect(link);
        SetStatus(sidechain
            ? $"Connected {from.Title} as a sidechain key."
            : $"Connected {from.Title} to {to.Title}.");
        PublishGraph();
    }

    private void SetLinkGain(string id, float value)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        if (link.Native && Math.Abs(value - 1f) > 1e-4f)
        {
            SetStatus("Disable native passthrough before changing the cable gain.", isError: true);
            return;
        }
        float old = link.Gain;
        bool enabled = link.Enabled;
        ApplyLinkSettings(id, value, enabled, publish: true);
        PushAction("Change send gain",
            undo: () => ApplyLinkSettings(id, old, enabled, publish: true),
            redo: () => ApplyLinkSettings(id, value, enabled, publish: true),
            coalesceKey: $"send-gain:{id}",
            structural: false);
    }

    private void SetLinkEnabled(string id, bool enabled)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        if (link.Native && !enabled)
        {
            SetStatus("Disable native passthrough before muting the cable.", isError: true);
            return;
        }
        float gain = link.Gain;
        bool old = link.Enabled;
        ApplyLinkSettings(id, gain, enabled, publish: true);
        PushAction(enabled ? "Enable send" : "Mute send",
            undo: () => ApplyLinkSettings(id, gain, old, publish: true),
            redo: () => ApplyLinkSettings(id, gain, enabled, publish: true),
            structural: false);
    }

    private void SetLinkNative(string id, bool enabled)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null || link.Native == enabled)
        {
            return;
        }

        if (!enabled && link.Native)
        {
            DisableNativeChainForLink(link);
            return;
        }

        if (link.Kind != "direct" ||
            !_nodes.TryGetValue(link.From, out var from) || from.Source == null ||
            !_nodes.TryGetValue(link.To, out var to) || to.Output == null)
        {
            SetStatus("Native passthrough needs a direct source-to-device cable.", isError: true);
            return;
        }

        if (enabled && _links.Any(other => other.Id != link.Id && other.To == link.To))
        {
            SetStatus("Native passthrough needs to be the only route to that output.", isError: true);
            return;
        }

        if (to.Output.IsRecording)
        {
            SetStatus("Stop recording before changing native passthrough.", isError: true);
            return;
        }

        if (enabled && (!link.Enabled || Math.Abs(link.Gain - 1f) > 1e-4f))
        {
            SetStatus("Set the cable to 100% and enabled before enabling native passthrough.", isError: true);
            return;
        }

        string? error;
        if (enabled)
        {
            error = _graph.SetNativePassthrough(to.Output, from.Source, true);
        }
        else
        {
            error = _graph.SetNativePassthrough(to.Output, from.Source, false);
            if (error == null)
            {
                link.Native = false;
            }
        }

        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        if (enabled)
        {
            link.Native = true;
        }

        PushAction(enabled ? "Enable native passthrough" : "Disable native passthrough",
            undo: () => ApplyLinkNative(id, !enabled, publish: true),
            redo: () => ApplyLinkNative(id, enabled, publish: true),
            structural: false);
        PublishLinkState(id);
        PublishTelemetry(force: true);
        SetStatus(enabled
            ? $"Native passthrough: {from.Title} → {to.Title}."
            : $"Normal routing restored for {to.Title}.");
    }

    private bool TryBuildNativeChain(
        OutputNode output,
        out SourceNode? source,
        out IAudioProcessor[] processors,
        out List<LinkEntry> links,
        out string reason)
    {
        source = null;
        processors = Array.Empty<IAudioProcessor>();
        links = new List<LinkEntry>();
        reason = "";

        var outputEntry = _nodes.Values.FirstOrDefault(entry => ReferenceEquals(entry.Output, output));
        if (outputEntry == null)
        {
            reason = "The output is no longer in the graph.";
            return false;
        }

        if (_links.Count(link => link.To == outputEntry.Id) != 1)
        {
            reason = "Native passthrough needs a single route to the output.";
            return false;
        }

        var modifiers = new List<ModifierNode>();
        string currentId = outputEntry.Id;
        for (int depth = 0; depth < 64; depth++)
        {
            var incoming = _links.Where(link => link.To == currentId && link.Native).ToList();
            if (incoming.Count != 1)
            {
                reason = "Every cable in a native chain must be native.";
                return false;
            }

            var link = incoming[0];
            if (_links.Count(candidate => candidate.To == currentId) != 1)
            {
                reason = "A native node cannot have another incoming route.";
                return false;
            }

            links.Add(link);
            if (!_nodes.TryGetValue(link.From, out var from))
            {
                reason = "A native chain node is missing.";
                return false;
            }

            if (from.Source != null)
            {
                source = from.Source;
                break;
            }

            if (from.Modifier == null)
            {
                reason = "A native chain must start at one source.";
                return false;
            }

            var modifier = from.Modifier;
            if (modifier.Parallel ||
                modifier.SidechainInputs.Count > 0 ||
                modifier.SidechainModifiers.Count > 0 ||
                modifier.Next.Count > 1 ||
                modifier.Outputs.Any(item => !ReferenceEquals(item, output)))
            {
                reason = "Parallel, sidechain or shared-route nodes cannot run in native passthrough.";
                return false;
            }

            modifiers.Add(modifier);
            currentId = from.Id;
        }

        if (source == null)
        {
            reason = "Native passthrough needs a source.";
            return false;
        }

        var chain = new List<IAudioProcessor>();
        for (int i = modifiers.Count - 1; i >= 0; i--)
        {
            var modifier = modifiers[i];
            if (!modifier.Enabled)
            {
                continue;
            }

            foreach (var processor in modifier.Processors)
            {
                if (processor is not INativeProcessor)
                {
                    reason = $"'{processor.DisplayName}' does not support native-rate processing.";
                    return false;
                }

                chain.Add(processor);
            }
        }

        processors = chain.ToArray();
        return true;
    }

    private bool RefreshNativeChain(OutputNode output)
    {
        if (!TryBuildNativeChain(output, out var source, out var processors,
                out _, out string reason))
        {
            SetStatus(reason, isError: true);
            return false;
        }

        string? error = _graph.SetNativePassthrough(output, source!, true, processors);
        if (error != null)
        {
            SetStatus(error, isError: true);
            return false;
        }

        return true;
    }

    private bool TryFindNativeOutput(LinkEntry link, out OutputNode? output)
    {
        output = null;
        string currentId = link.To;
        for (int depth = 0; depth < 64; depth++)
        {
            if (!_nodes.TryGetValue(currentId, out var entry))
            {
                return false;
            }

            if (entry.Output != null)
            {
                output = entry.Output;
                return true;
            }

            var next = _links.FirstOrDefault(candidate =>
                candidate.From == currentId && candidate.Native);
            if (next == null)
            {
                return false;
            }

            currentId = next.To;
        }

        return false;
    }

    private void DisableNativeChainForLink(LinkEntry link)
    {
        if (!TryFindNativeOutput(link, out var output) || output == null)
        {
            return;
        }

        if (TryBuildNativeChain(output, out _, out _, out var links, out _))
        {
            foreach (var pathLink in links)
            {
                pathLink.Native = false;
            }
        }

        if (output.NativeSource != null)
        {
            _graph.SetNativePassthrough(output, output.NativeSource, false);
        }

        PublishGraph();
        SetStatus("Native passthrough disabled.");
    }

    private void ApplyLinkNative(string id, bool enabled, bool publish)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null || link.Native == enabled)
        {
            return;
        }

        if (!_nodes.TryGetValue(link.From, out var from) || from.Source == null ||
            !_nodes.TryGetValue(link.To, out var to) || to.Output == null)
        {
            return;
        }

        string? error = _graph.SetNativePassthrough(to.Output, from.Source, enabled);
        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        link.Native = enabled;
        if (publish)
        {
            PublishLinkState(id);
            PublishTelemetry(force: true);
        }
    }

    private void ActivateNativeRoutes()
    {
        foreach (var link in _links.Where(item => item.Native && item.Kind == "direct").ToList())
        {
            if (!_nodes.TryGetValue(link.From, out var from) || from.Source == null ||
                !_nodes.TryGetValue(link.To, out var to) || to.Output == null)
            {
                link.Native = false;
                continue;
            }

            string? error = _graph.SetNativePassthrough(to.Output, from.Source, true);
            if (error != null)
            {
                link.Native = false;
                SetStatus($"Native passthrough unavailable for {to.Title}: {error}", isError: true);
            }
        }
    }

    private void ApplyLinkSettings(string id, float gain, bool enabled, bool publish)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        link.Gain = Math.Clamp(gain, 0f, 1.5f);
        link.Enabled = enabled;
        ApplyLinkRouting(link, publish);
    }

    private void ApplyLinkRouting(LinkEntry link, bool publish)
    {
        float gain = link.Enabled ? link.Gain : 0f;
        if (_nodes.TryGetValue(link.From, out var from) && _nodes.TryGetValue(link.To, out var to))
        {
            if (link.Kind == "direct" && from.Source != null && to.Output != null)
            {
                _graph.SetRouteGain(from.Source, to.Output, gain);
            }
            else if (link.Kind == "modifier-output" && from.Modifier != null && to.Output != null)
            {
                _graph.SetModifierOutputGain(from.Modifier, to.Output, gain);
            }
            else if (link.Kind == "sidechain")
            {
                SetNoteKey(from, to, link.Enabled);
            }
        }

        if (publish) PublishLinkState(link.Id);
    }

    private static void SetNoteKey(NodeEntry from, NodeEntry to, bool enabled)
    {
        if (from.Modifier == null || to.Modifier == null)
        {
            return;
        }

        int tapId = enabled
            ? from.Modifier.Processors.OfType<NoteDetectorProcessor>().FirstOrDefault()?.TapId ?? 0
            : 0;
        if (tapId <= 0)
        {
            return;
        }

        foreach (var tune in to.Modifier.Processors.OfType<AutoTuneProcessor>())
        {
            tune.SetKeyTapId(tapId);
        }

        foreach (var tune in to.Modifier.Processors.OfType<AutoTuneExperimentalProcessor>())
        {
            tune.SetKeyTapId(tapId);
        }
    }

    private LinkEntry ConnectEntries(NodeEntry from, NodeEntry to, float gain = 1, bool publish = false, string? linkId = null, string? kind = null)
    {
        if (to.Output != null)
        {
            // Inactive file recorders join the graph lazily, on first route.
            _graph.AddOutputNode(to.Output);
        }

        if (kind == "sidechain")
        {
            if (to.Modifier == null ||
                !to.Modifier.Processors.Any(SidechainRouting.Supports))
            {
                throw new InvalidOperationException($"Cannot use {from.Title} as a sidechain key for {to.Title}.");
            }

            if (from.Source != null)
            {
                _graph.ConnectSourceToModifierSidechain(from.Source, to.Modifier);
            }
            else if (from.Modifier != null)
            {
                _graph.ConnectModifierToModifierSidechain(from.Modifier, to.Modifier);
            }
            else
            {
                throw new InvalidOperationException($"Cannot use {from.Title} as a sidechain key for {to.Title}.");
            }

            SetNoteKey(from, to, true);
        }
        else if (from.Source != null && to.Output != null)
        {
            kind = "direct";
            _graph.Connect(from.Source, to.Output, gain);
        }
        else if (from.Source != null && to.Modifier != null)
        {
            kind = "source-modifier";
            _graph.ConnectSourceToModifier(from.Source, to.Modifier);
        }
        else if (from.Modifier != null && to.Output != null)
        {
            kind = "modifier-output";
            _graph.ConnectModifierToOutput(from.Modifier, to.Output, gain);
        }
        else if (from.Modifier != null && to.Modifier != null)
        {
            kind = "modifier-modifier";
            _graph.ConnectModifierToModifier(from.Modifier, to.Modifier);
        }
        else
        {
            throw new InvalidOperationException($"Cannot connect {from.Title} to {to.Title}.");
        }

        var link = new LinkEntry
        {
            Id = linkId ?? Guid.NewGuid().ToString("N"),
            From = from.Id,
            To = to.Id,
            Kind = kind!,
            Gain = gain,
        };
        _links.Add(link);
        if (publish) PublishGraph();
        return link;
    }

    private void DisconnectLink(string id)
    {
        var link = _links.FirstOrDefault(item => item.Id == id);
        if (link == null) return;
        var captured = CloneLink(link);
        RemoveLinkInternal(link);
        PushDisconnect(captured);
        SetStatus("Cable disconnected.");
        PublishGraph();
    }

    private void RemoveLinkInternal(LinkEntry link, bool releaseNative = true)
    {
        var current = _links.FirstOrDefault(item => item.Id == link.Id);
        if (current == null)
        {
            return;
        }

        if (releaseNative && current.Native)
        {
            DisableNativeChainForLink(current);
        }

        if (_nodes.TryGetValue(current.From, out var from) && _nodes.TryGetValue(current.To, out var to))
        {

            switch (current.Kind)
            {
                case "direct" when from.Source != null && to.Output != null:
                    _graph.Disconnect(from.Source, to.Output);
                    break;
                case "sidechain" when from.Source != null && to.Modifier != null:
                    _graph.DisconnectSourceFromModifierSidechain(from.Source, to.Modifier);
                    break;
                case "sidechain" when from.Modifier != null && to.Modifier != null:
                    _graph.DisconnectModifierFromModifierSidechain(from.Modifier, to.Modifier);
                    break;
                case "source-modifier" when from.Source != null && to.Modifier != null:
                    _graph.DisconnectSourceFromModifier(from.Source, to.Modifier);
                    break;
                case "modifier-output" when from.Modifier != null && to.Output != null:
                    _graph.DisconnectModifierFromOutput(from.Modifier, to.Output);
                    break;
                case "modifier-modifier" when from.Modifier != null && to.Modifier != null:
                    _graph.DisconnectModifierFromModifier(from.Modifier, to.Modifier);
                    break;
            }

            if (current.Kind == "sidechain")
            {
                SetNoteKey(from, to, false);
            }
        }

        _links.Remove(current);
    }

    private void DisconnectNode(string id)
    {
        var links = _links.Where(link => link.From == id || link.To == id).Select(CloneLink).ToList();
        if (links.Count == 0) return;

        foreach (var link in _links.Where(link => link.From == id || link.To == id).ToList())
        {
            RemoveLinkInternal(link);
        }

        PushAction("Disconnect routes",
            undo: () =>
            {
                RestoreLinks(links);
                PublishGraph();
            },
            redo: () =>
            {
                foreach (var link in links) RemoveLinkInternal(link);
                PublishGraph();
            });

        SetStatus("Node routes disconnected.");
        PublishGraph();
    }

    private void RemoveNode(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        var snapshot = CaptureNode(entry);
        var links = _links.Where(link => link.From == id || link.To == id).Select(CloneLink).ToList();
        var upstream = entry.Modifier == null
            ? new List<LinkEntry>()
            : links.Where(link => link.To == id && link.Kind != "sidechain").ToList();
        var downstream = entry.Modifier == null
            ? new List<LinkEntry>()
            : links.Where(link => link.From == id && link.Kind != "sidechain").ToList();
        var bridges = new List<LinkEntry>();

        RemoveEntryInternal(id);
        CreateBridges(upstream, downstream, bridges);

        PushAction("Remove node",
            undo: () =>
            {
                foreach (var bridge in bridges) RemoveLinkInternal(bridge);
                bridges.Clear();
                RestoreNodeSnapshot(snapshot);
                RestoreLinks(links);
                PublishGraph();
            },
            redo: () =>
            {
                RemoveEntryInternal(entry.Id);
                CreateBridges(upstream, downstream, bridges);
                PublishGraph();
            });

        SetStatus($"Removed {entry.Title}.");
        PublishGraph();
    }

    /// <summary>
    /// Keeps a chain intact when its middle node is removed: every upstream
    /// node is joined to every downstream node that the removed node fed.
    /// </summary>
    private void CreateBridges(List<LinkEntry> upstream, List<LinkEntry> downstream, List<LinkEntry> bridges)
    {
        bridges.Clear();
        foreach (var incoming in upstream)
        {
            if (!_nodes.TryGetValue(incoming.From, out var from)) continue;

            foreach (var outgoing in downstream)
            {
                if (!_nodes.TryGetValue(outgoing.To, out var to) || from.Id == to.Id) continue;
                if (_links.Any(link => link.From == from.Id && link.To == to.Id && link.Kind != "sidechain")) continue;

                try
                {
                    bridges.Add(ConnectEntries(from, to, incoming.Gain));
                }
                catch
                {
                    // A stale endpoint should not prevent removing the node.
                }
            }
        }
    }

    private void RemoveEntryInternal(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        bool linked = _links.Any(link => link.From == id || link.To == id);
        bool hasTap = entry.Modifier?.Processors.Any(processor => processor is ITapProcessor) == true;
        bool affectsEngine = linked || hasTap;
        foreach (var link in _links.Where(link => link.From == id || link.To == id).ToList())
        {
            RemoveLinkInternal(link);
        }

        if (entry.Source != null) _graph.RemoveSource(entry.Source, affectsEngine);
        if (entry.Output != null) _graph.RemoveOutput(entry.Output, affectsEngine || entry.Output.IsRunning);
        if (entry.Modifier != null)
        {
            foreach (var processor in entry.Modifier.Processors)
            {
                CleanupTapProcessor(processor);
            }

            _graph.RemoveModifier(entry.Modifier, affectsEngine);
        }

        _nodes.Remove(id);
        _nodeClipHoldUntil.Remove(id);
        _spectrogramCursors.Remove(id);
        _midiBindings.RemoveAll(binding => binding.NodeId == id);
        if (_midiLearnTarget?.NodeId == id)
        {
            _midiLearnTarget = null;
        }
    }

    private static void CleanupTapProcessor(IAudioProcessor processor)
    {
        switch (processor)
        {
            case TranscriptionProcessor transcriber:
                TranscriptionRegistry.Stop(transcriber.TapId);
                TranscriptionProcessor.RemoveTap(transcriber.TapId);
                break;
            case TranslatorProcessor translator:
                TranslationRegistry.Stop(translator.TapId);
                TranscriptionProcessor.RemoveTap(translator.TapId);
                break;
            case SpectrumProcessor spectrum:
                SpectrumProcessor.RemoveTap(spectrum.TapId);
                break;
            case SpectrogramProcessor spectrogram:
                SpectrogramProcessor.RemoveTap(spectrogram.TapId);
                break;
            case WaveformProcessor waveform:
                WaveformProcessor.RemoveTap(waveform.TapId);
                break;
            case DbMeterProcessor dbMeter:
                DbMeterProcessor.RemoveTap(dbMeter.TapId);
                break;
            case LoudnessProcessor loudness:
                LoudnessProcessor.RemoveTap(loudness.TapId);
                break;
            case NoteDetectorProcessor note:
                NoteDetectorProcessor.RemoveTap(note.TapId);
                break;
            case ChainProcessor chain:
                foreach (var inner in chain.Processors)
                {
                    CleanupTapProcessor(inner);
                }
                break;
            case ParallelChainProcessor parallel:
                foreach (var inner in parallel.Processors)
                {
                    CleanupTapProcessor(inner);
                }
                break;
        }
    }

    private void ClearGraph()
    {
        _graph.AutoRun = false;
        foreach (var entry in _nodes.Values)
        {
            if (entry.Modifier == null)
            {
                continue;
            }

            foreach (var processor in entry.Modifier.Processors)
            {
                CleanupTapProcessor(processor);
            }
        }

        TranscriptionRegistry.StopAll();
        TranslationRegistry.StopAll();
        DisposeInactiveOutputs();
        _graph.Dispose();
        _graph = new AudioGraph { AutoRun = false };
        _nodes.Clear();
        _links.Clear();
        _frames.Clear();
        _spectrogramCursors.Clear();
        ClearMidiBindings();
        _running = false;
        PublishTelemetry();
    }

    /// <summary>Disposes recorders that are not in the render graph yet.</summary>
    private void DisposeInactiveOutputs()
    {
        foreach (var entry in _nodes.Values)
        {
            if (entry.Output is { } output && !_graph.Outputs.Contains(output))
            {
                output.Dispose();
            }
        }
    }

    private void StartRouting()
    {
        _graph.AutoRun = true;
        _graph.StartEngine();
        int started = 0;
        int recordings = 0;
        int armed = 0;
        int unarmed = 0;
        int playersWithoutFile = 0;

        // Start outputs before sources. Output initialization can take hundreds
        // of milliseconds; starting capture first leaves a permanent source
        // backlog that later shows up as stale skips and clicks.
        foreach (var entry in _nodes.Values)
        {
            if (entry.Output is not { IsRunning: false } output)
            {
                continue;
            }

            try
            {
                if (output.IsFileRecorder)
                {
                    if (!output.AutoStart)
                    {
                        continue;
                    }

                    if (output.RecorderMode == RecorderMode.On)
                    {
                        if (StartRecorder(entry, output)) recordings++;
                        else if (!output.AutoSave && !output.HasRecordingPath) unarmed++;
                    }
                    else if (output.RecorderMode == RecorderMode.Armed)
                    {
                        armed++;
                        if (!output.AutoSave && !output.HasRecordingPath) unarmed++;
                    }
                    else if (!output.AutoSave && !output.HasRecordingPath)
                    {
                        unarmed++;
                    }

                    continue;
                }

                output.Start();
            }
            catch (Exception ex)
            {
                ShowError($"Could not start '{entry.Title}': {ex.Message}", ex);
            }
        }

        foreach (var entry in _nodes.Values)
        {
            if (entry.Source is not { IsRunning: false } source)
            {
                continue;
            }

            try
            {
                if (source.Kind == SourceKind.Tts)
                {
                    continue;
                }
                if (source.Kind == SourceKind.File && !source.HasFile)
                {
                    playersWithoutFile++;
                    continue;
                }

                source.Start();
                started++;
            }
            catch (Exception ex)
            {
                ShowError($"Could not start '{entry.Title}': {ex.Message}", ex);
            }
        }
        ActivateNativeRoutes();
        _graph.StartEngine();
        _running = _graph.Outputs.Any(output => output.IsRunning);
        int outputCount = _graph.Outputs.Count;
        string recordingNote = recordings > 0 ? $", {recordings} recording(s)" : string.Empty;
        string armedNote = armed > 0 ? $", {armed} armed" : string.Empty;
        string unarmedNote = unarmed > 0 ? $"; {unarmed} recorder(s) need a file" : string.Empty;
        string playerNote = playersWithoutFile > 0
            ? $"; {playersWithoutFile} file player(s) need a file"
            : string.Empty;
        SetStatus($"Routing started · {started} source(s), {outputCount} output(s){recordingNote}{unarmedNote}{playerNote}.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void StopRouting()
    {
        var recorders = _nodes.Values.Select(entry => entry.Output)
            .Where(output => output?.IsRecording == true).Cast<OutputNode>().ToList();
        _graph.AutoRun = false;
        _graph.StopEngine();
        foreach (var source in _graph.Sources) source.Stop();
        foreach (var output in _graph.Outputs) output.Stop();

        int saved = recorders.Count(output => string.IsNullOrEmpty(output.RecordingError));
        int failed = recorders.Count - saved;
        _running = false;
        SetStatus(recorders.Count == 0
            ? "Routing stopped."
            : $"Routing stopped · {saved} recording(s) saved{(failed > 0 ? $", {failed} failed" : string.Empty)}.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void SetMicrophoneMute(bool muted)
    {
        _micMuted = muted;
        int affected = 0;
        foreach (var source in _graph.Sources.Where(source => source.Kind == SourceKind.Device))
        {
            source.Muted = muted;
            affected++;
        }

        SetStatus(muted ? $"Microphones muted · {affected} input(s)." : "Microphones live again.");
        PublishGraph();
        PublishTelemetry(force: true);
    }

    private void ChooseRecorderFile(string id, string formatName)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Output is not { IsFileRecorder: true } output) return;
        var format = AudioFileFormats.Get(AudioFileFormats.Parse(formatName));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Choose recording destination",
            Filter = format.Filter,
            FileName = output.HasRecordingPath ? output.RecordingPath : $"ruse-recording.{format.Extension}",
            DefaultExt = format.Extension,
            AddExtension = true,
        };
        if (dialog.ShowDialog(_window) != true) return;

        output.SetRecordingDestination(dialog.FileName, format.Format);
        entry.Title = output.Title;
        if (_graph.AutoRun && output.RecorderMode == RecorderMode.On)
        {
            StartRecorder(entry, output);
        }

        SetStatus($"Recorder destination set: {Path.GetFileName(dialog.FileName)}");
        PublishGraph();
    }

    private void StopRecorder(string id)
    {
        SetRecorderMode(id, RecorderMode.Off.ToString());
    }

    private void SetRecorderFormat(string id, string formatName)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Output is not { IsFileRecorder: true } output) return;
        var format = AudioFileFormats.Get(AudioFileFormats.Parse(formatName));
        string path = output.HasRecordingPath
            ? Path.ChangeExtension(output.RecordingPath, format.Extension)
            : string.Empty;
        output.SetRecordingDestination(path, format.Format);
        if (!AudioFileFormats.SupportsSampleRate(format.Format, output.RecorderSampleRate))
        {
            output.SetRecorderSampleRate(48000);
        }

        entry.Title = string.IsNullOrEmpty(path) ? "File recorder" : output.Title;
        output.Title = entry.Title;
        PublishGraph();
    }

    private void SetRecorderSampleRate(string id, int sampleRate)
    {
        if (!_nodes.TryGetValue(id, out var entry) || entry.Output is not { IsFileRecorder: true } output)
        {
            return;
        }

        if (output.IsRecording)
        {
            SetStatus("Stop recording before changing the sample rate.", isError: true);
            return;
        }

        if (!AudioFileFormats.SupportsSampleRate(output.RecordingFormat, sampleRate))
        {
            SetStatus(
                $"{AudioFileFormats.Get(output.RecordingFormat).DisplayName} does not support {sampleRate} Hz recording.",
                isError: true);
            return;
        }

        output.SetRecorderSampleRate(sampleRate);
        PublishGraph();
    }

    private void DuplicateNode(string id)
    {
        if (!_nodes.TryGetValue(id, out var entry)) return;
        if (entry.Output != null)
        {
            SetStatus("Outputs and recorders cannot be duplicated.", isError: true);
            return;
        }

        var copy = CaptureNode(entry) with
        {
            Id = Guid.NewGuid().ToString("N"),
            X = entry.X + 40,
            Y = entry.Y + 40,
            Title = entry.Title,
        };
        var restored = RestoreNodeSnapshot(copy);
        if (restored == null) return;
        var redoSnapshot = CaptureNode(restored);
        PushAction("Duplicate node",
            undo: () =>
            {
                RemoveEntryInternal(restored.Id);
                PublishGraph();
            },
            redo: () =>
            {
                RestoreNodeSnapshot(redoSnapshot);
                PublishGraph();
            });
        SetStatus($"Duplicated '{entry.Title}'.");
        PublishGraph();
    }

    private void MoveNodes(BridgeMessage message)
    {
        if (!message.Payload.TryGetProperty("positions", out var positions) ||
            positions.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var changes = new List<(string Id, double OldX, double OldY, double NewX, double NewY)>();
        foreach (var item in positions.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idValue) ||
                !item.TryGetProperty("x", out var xValue) ||
                !item.TryGetProperty("y", out var yValue) ||
                idValue.GetString() is not { } nodeId ||
                !_nodes.TryGetValue(nodeId, out var entry))
            {
                continue;
            }

            double x = xValue.GetDouble();
            double y = yValue.GetDouble();
            changes.Add((nodeId, entry.X, entry.Y, x, y));
            entry.X = x;
            entry.Y = y;
        }

        if (changes.Count == 0) return;
        PushAction("Tidy graph",
            undo: () =>
            {
                foreach (var change in changes) SetNodePosition(change.Id, change.OldX, change.OldY, publish: false);
                PublishGraph();
            },
            redo: () =>
            {
                foreach (var change in changes) SetNodePosition(change.Id, change.NewX, change.NewY, publish: false);
                PublishGraph();
            });
        PublishGraph();
    }

    private void BuildLiveGraph(uint processId)
    {
        _graph.AutoRun = false;
        var source = RegisterSource(new SourceNode("Live audio", SourceKind.Application, processId), 40, 140);
        var modifier = AddEffect(HighPassProcessor.Id, 400, 140);
        var output = _catalog.Outputs.FirstOrDefault() is { } first
            ? AddDeviceOutput(first.Id, 740, 140)
            : null;
        ConnectEntries(source, modifier);
        if (output != null)
        {
            ConnectEntries(modifier, output);
        }

        SetStatus("Live graph · carrying real audio.");
        StartRouting();
    }

    private void BuildDemoGraph()
    {
        _graph.AutoRun = false;
        var discord = RegisterSource(new SourceNode("Discord", SourceKind.Application, 1234), 40, 40);
        var game = RegisterSource(new SourceNode("Game (Counter-Strike)", SourceKind.Application, 5678), 40, 280);
        var mic = RegisterSource(new SourceNode("Microphone", SourceKind.Device, deviceId: _catalog.Inputs.FirstOrDefault()?.Id), 40, 560);
        var spotify = RegisterSource(new SourceNode("Spotify", SourceKind.Application, 91011), 40, 800);
        var highPass = AddEffect(HighPassProcessor.Id, 390, 250);
        var compressor = AddEffect("compressor", 680, 500);

        var headphones = _catalog.Outputs.FirstOrDefault() is { } first ? AddDeviceOutput(first.Id, 900, 70) : null;
        var virtualOutput = _catalog.Virtuals.FirstOrDefault() is { } virtualItem ? AddVirtualOutput(virtualItem.Id, 930, 560) : null;
        var recorder = RegisterOutput(OutputNode.CreateForFile(string.Empty, AudioFileFormat.Wav24), 930, 850);

        if (headphones != null)
        {
            ConnectEntries(discord, headphones);
            ConnectEntries(game, headphones);
            ConnectEntries(spotify, headphones);
        }

        ConnectEntries(mic, highPass);
        ConnectEntries(highPass, compressor);
        if (virtualOutput != null)
        {
            ConnectEntries(compressor, virtualOutput);
            ConnectEntries(discord, virtualOutput);
        }

        ConnectEntries(compressor, recorder);
        StartRouting();
    }
}
