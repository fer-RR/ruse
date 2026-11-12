using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using NAudio.Wave;
using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    /// <summary>Bridge/undo/chain checks for --web-test; no audio hardware.</summary>
    internal string RunParitySelfTest()
    {
        SubgraphLibrary.Load();
        var checks = new List<string>();
        bool pass = true;

        Check("non-finite telemetry", Meter(float.NaN) == 0 &&
                                      Meter(float.PositiveInfinity) == 0 &&
                                      Finite(double.NegativeInfinity) == 0);

        string nodeStateJson = JsonSerializer.Serialize(new NodeStateDto
        {
            Id = "node",
            Volume = 0.5f,
            Muted = true,
            Send = 0.25f,
            Parallel = true,
            Color = "",
            Accent = "#4F8DFF",
        }, _json);
        Check("node-state delta contract",
            nodeStateJson.Contains("\"volume\":0.5", StringComparison.Ordinal) &&
            nodeStateJson.Contains("\"muted\":true", StringComparison.Ordinal) &&
            nodeStateJson.Contains("\"send\":0.25", StringComparison.Ordinal) &&
            nodeStateJson.Contains("\"parallel\":true", StringComparison.Ordinal) &&
            !nodeStateJson.Contains("\"solo\"", StringComparison.Ordinal) &&
            !nodeStateJson.Contains("\"enabled\"", StringComparison.Ordinal));

        const int fftFrames = 4096;
        var fftTone = new float[fftFrames * 2];
        for (int frame = 0; frame < fftFrames; frame++)
        {
            float sample = 0.5f * MathF.Sin(2f * MathF.PI * 1000f * frame / 48000f);
            fftTone[frame * 2] = sample;
            fftTone[frame * 2 + 1] = sample;
        }

        var spectrum = new SpectrumProcessor();
        spectrum.SetTapId(SpectrumProcessor.NextTapId());
        spectrum.Process(fftTone, fftFrames);
        var bands = SpectrumProcessor.GetBands(spectrum.TapId);
        Check("spectrum tap", bands is { Length: 256 } &&
                              bands.Max() > -60f &&
                              SpectrumProcessor.GetPeakHz(spectrum.TapId) is > 900f and < 1100f);
        SpectrumProcessor.RemoveTap(spectrum.TapId);

        var highTone = new float[fftFrames * 2];
        for (int frame = 0; frame < fftFrames; frame++)
        {
            float sample = (float)(0.5 * Math.Sin(2.0 * Math.PI * 10000.0 * frame / 48000.0));
            highTone[frame * 2] = sample;
            highTone[frame * 2 + 1] = sample;
        }

        var highSpectrum = new SpectrumProcessor();
        highSpectrum.Parameters.First(parameter => parameter.Name == "Ceiling").Set(0f);
        highSpectrum.SetTapId(SpectrumProcessor.NextTapId());
        for (int block = 0; block < 6; block++)
        {
            highSpectrum.Process(highTone, fftFrames);
        }

        var highBands = SpectrumProcessor.GetBands(highSpectrum.TapId);
        int highBand = (int)Math.Floor(256 * Math.Log(10000.0 / 20.0) / Math.Log(1000.0));
        Check("spectrum high band", highBands is { Length: 256 } &&
                                    Enumerable.Range(highBand - 1, 3)
                                        .Max(index => highBands[index]) > -12f);
        SpectrumProcessor.RemoveTap(highSpectrum.TapId);

        var spectrogram = new SpectrogramProcessor();
        spectrogram.SetTapId(SpectrogramProcessor.NextTapId());
        spectrogram.Process(fftTone, fftFrames);
        var columns = SpectrogramProcessor.GetColumns(spectrogram.TapId);
        Check("spectrogram tap", columns is { Length: SpectrogramProcessor.Columns * 256 } &&
                                 columns.Any(value => value > 0));
        SpectrogramProcessor.RemoveTap(spectrogram.TapId);

        var waveform = new WaveformProcessor();
        waveform.SetTapId(WaveformProcessor.NextTapId());
        waveform.Process(fftTone, fftFrames);
        var waveformReading = WaveformProcessor.GetReading(waveform.TapId, WaveformProcessor.TriggerRising);
        Check("waveform tap", waveformReading is { } wave &&
                              wave.Samples.Length == WaveformProcessor.SampleCount &&
                              wave.Peak is > 0.45f and < 0.55f &&
                              Math.Abs(wave.Samples[0]) < 0.07f &&
                              wave.Samples[1] > wave.Samples[0]);
        WaveformProcessor.RemoveTap(waveform.TapId);

        var dbMeter = new DbMeterProcessor();
        dbMeter.SetTapId(DbMeterProcessor.NextTapId());
        var quiet = new float[480 * 2];
        for (int block = 0; block < 160; block++)
        {
            for (int frame = 0; frame < 480; frame++)
            {
                float sample = 0.0005f * MathF.Sin(2f * MathF.PI * 1000f * frame / 48000f);
                quiet[frame * 2] = sample;
                quiet[frame * 2 + 1] = sample;
            }

            dbMeter.Process(quiet, 480);
        }

        float lowDb = DbMeterProcessor.GetDb(dbMeter.TapId);
        Array.Clear(quiet);
        for (int block = 0; block < 80; block++) dbMeter.Process(quiet, 480);
        float silentDb = DbMeterProcessor.GetDb(dbMeter.TapId);
        Check("db meter low signal", lowDb is > -90f and < -45f && silentDb < lowDb);
        DbMeterProcessor.RemoveTap(dbMeter.TapId);

        var stereoMeter = new DbMeterProcessor();
        stereoMeter.SetTapId(DbMeterProcessor.NextTapId());
        var stereoBlock = new float[480 * 2];
        for (int frame = 0; frame < 480; frame++)
        {
            stereoBlock[frame * 2] = 0.4f;
            stereoBlock[frame * 2 + 1] = 0.02f;
        }

        for (int block = 0; block < 20; block++)
        {
            stereoMeter.Process(stereoBlock, 480);
        }

        Check("db meter stereo",
            DbMeterProcessor.GetDbLeft(stereoMeter.TapId) >
            DbMeterProcessor.GetDbRight(stereoMeter.TapId) + 6f &&
            DbMeterProcessor.GetPeakDbLeft(stereoMeter.TapId) >
            DbMeterProcessor.GetPeakDbRight(stereoMeter.TapId) + 6f);
        DbMeterProcessor.RemoveTap(stereoMeter.TapId);

        var loudness = new LoudnessProcessor();
        loudness.SetTapId(LoudnessProcessor.NextTapId());
        var loudnessBlock = new float[480 * 2];
        for (int frame = 0; frame < 480; frame++)
        {
            float sample = 0.25f * MathF.Sin(2f * MathF.PI * 1000f * frame / 48000f);
            loudnessBlock[frame * 2] = sample;
            loudnessBlock[frame * 2 + 1] = sample;
        }

        for (int block = 0; block < 160; block++)
        {
            loudness.Process(loudnessBlock, 480);
        }

        Check("loudness tap",
            LoudnessProcessor.GetMomentaryLufs(loudness.TapId) > -40f &&
            LoudnessProcessor.GetIntegratedLufs(loudness.TapId) > -40f &&
            LoudnessProcessor.GetTruePeakDb(loudness.TapId) > -12f);
        LoudnessProcessor.RemoveTap(loudness.TapId);
        Check("loudness tap cleanup",
            LoudnessProcessor.GetMomentaryLufs(loudness.TapId) <= -119f);

        var transcriber = new TranscriptionProcessor();
        transcriber.SetTapId(TranscriptionProcessor.NextTapId());
        const int toneFrames = 480;
        var tone = new float[toneFrames * 2];
        for (int frame = 0; frame < toneFrames; frame++)
        {
            float sample = 0.5f * MathF.Sin(2f * MathF.PI * 1000f * frame / 48000f);
            tone[frame * 2] = sample;
            tone[frame * 2 + 1] = sample;
        }

        transcriber.Process(tone, toneFrames);
        var tapBytes = new byte[4096];
        int tapRead = TranscriptionProcessor.ReadAvailable(transcriber.TapId, tapBytes);
        Check("transcriber resample", tapRead == 320 &&
                                      TranscriptionProcessor.GetLevel(transcriber.TapId) > 0.1f &&
                                      TranscriptionProcessor.GetLastAudioTick(transcriber.TapId) > 0);
        TranscriptionProcessor.RemoveTap(transcriber.TapId);

        var translator = new TranslatorProcessor();
        translator.SetTapId(TranscriptionProcessor.NextTapId());
        translator.Process(tone, toneFrames);
        int translatorTapRead = TranscriptionProcessor.ReadAvailable(translator.TapId, tapBytes);
        Check("translator resample", translatorTapRead == 320 &&
                                     TranscriptionProcessor.GetLevel(translator.TapId) > 0.1f);
        TranscriptionProcessor.RemoveTap(translator.TapId);

        Check("translator catalog",
            AudioProcessorCatalog.Find(TranslatorProcessor.Id) != null &&
            TranslationLanguages.Normalize("pt-br") == "pt-BR" &&
            TranslationLanguages.Normalize("unknown") == TranslationLanguages.DefaultCode &&
            TranslationLanguages.NeedsModel("pt-BR") &&
            !TranslationLanguages.NeedsModel("en"));

        Check("translation model catalog",
            TranslationModels.All.Count >= 3 &&
            TranslationModels.Find(TranslationModels.DefaultId) != null &&
            TranslationModels.All.All(model => model.Url.EndsWith(".gguf", StringComparison.Ordinal)) &&
            TranslationModels.All.All(model => model.License == "Apache-2.0") &&
            TranslationModels.FormatSize(600L * 1024 * 1024).Contains("MB"));

        var retarget = new SourceNode("App retarget", SourceKind.Application, 111);
        retarget.RetargetApplication(222, "testapp");
        Check("app retarget", retarget.ProcessId == 222 && retarget.ProcessName == "testapp");
        retarget.Dispose();

        Check("speech model catalog", TranscriptionModels.All.Count >= 8 &&
                                      TranscriptionModels.Find(TranscriptionModels.DefaultId) != null &&
                                      TranscriptionModels.All.All(model => !string.IsNullOrEmpty(model.Tier)) &&
                                      TranscriptionModels.All.Any(model => model.Tier == "Base") &&
                                      TranscriptionModels.All.Any(model => model.Tier == "Small") &&
                                      TranscriptionModels.All.Any(model => model.Tier == "Large") &&
                                      TranscriptionModels.All.All(model => model.Url.EndsWith(".bin", StringComparison.Ordinal)) &&
                                      TranscriptionModels.All.All(model => model.Note?.Contains("MIT", StringComparison.Ordinal) == true) &&
                                      TranscriptionModels.FormatSize(40L * 1024 * 1024).Contains("MB"));

        var sentenceCues = SentenceSplitter.Split("Hello world. How are you?", 1, 3);
        var srt = SubtitleExporter.Srt(sentenceCues);
        var vtt = SubtitleExporter.Vtt(sentenceCues);
        Check("sentence segmentation",
            sentenceCues.Count == 2 &&
            sentenceCues[0].Text == "Hello world." &&
            sentenceCues[0].StartSeconds == 1 &&
            sentenceCues[1].EndSeconds == 3);
        Check("subtitle export",
            srt.Contains("00:00:01,000 --> 00:00:02,000", StringComparison.Ordinal) &&
            srt.Contains("Hello world.", StringComparison.Ordinal) &&
            vtt.StartsWith("WEBVTT", StringComparison.Ordinal) &&
            vtt.Contains("00:00:01.000 --> 00:00:02.000", StringComparison.Ordinal));

        var chunker = new SpeechChunker();
        var vadSamples = new float[16000 * 5];
        for (int i = 0; i < 16000 * 2; i++)
        {
            vadSamples[i] = 0.05f * MathF.Sin(2f * MathF.PI * 180f * i / 16000f);
        }

        bool vadCut = chunker.Add(vadSamples, out var vadChunk, out double vadStart);
        Check("vad chunking",
            vadCut &&
            vadChunk.Length >= 16000 * 2 &&
            Math.Abs(vadStart) < 0.001);

        string fileTestPath = Path.Combine(Path.GetTempPath(), $"ruse-file-test-{Guid.NewGuid():N}.wav");
        bool fileTransportOk = false;
        try
        {
            using (var writer = new WaveFileWriter(fileTestPath, new WaveFormat(48000, 16, 2)))
            {
                var seconds = new byte[48000 * 2 * 2];
                for (int i = 0; i < 48000; i++)
                {
                    short sample = (short)(8000 * Math.Sin(2 * Math.PI * 220 * i / 48000.0));
                    seconds[i * 4] = (byte)sample;
                    seconds[i * 4 + 1] = (byte)(sample >> 8);
                    seconds[i * 4 + 2] = (byte)sample;
                    seconds[i * 4 + 3] = (byte)(sample >> 8);
                }

                writer.Write(seconds, 0, seconds.Length);
                writer.Write(seconds, 0, seconds.Length);
            }

            using var file = new AudioFileCapture(fileTestPath, loop: false);
            var fileSource = new SourceNode("File test", SourceKind.File);
            fileSource.SetFile(fileTestPath, loop: false);
            file.Start();
            Thread.Sleep(300);
            file.Pause();
            float pausedPosition = file.PositionSeconds;
            Thread.Sleep(200);
            bool pausedStable = Math.Abs(file.PositionSeconds - pausedPosition) < 0.08f;
            file.Seek(1.0);
            Thread.Sleep(80);
            file.Resume();
            Thread.Sleep(250);
            fileTransportOk = file.DurationSeconds >= 1.9 &&
                              pausedStable &&
                              file.PositionSeconds >= 1.05f &&
                              Math.Abs(fileSource.FileDurationSeconds - file.DurationSeconds) < 0.05;
            file.Stop();
            fileSource.Dispose();
        }
        catch
        {
            fileTransportOk = false;
        }
        finally
        {
            try { File.Delete(fileTestPath); } catch { }
        }

        Check("file player transport", fileTransportOk);

        var appSettings = new AppSettings
        {
            Width = 1280,
            Height = 720,
            Left = 0,
            Top = 0,
            Theme = "light",
            UseGpu = true,
            IsolateVst3 = false,
            TranslationProvider = "opus",
            ConnectorAnimation = false,
            MinimizeToTray = true,
            TrayPromptShown = true,
            ShowWelcomeScreen = false,
            AggressiveOutputCushion = true,
            MidiEnabled = true,
            MidiInputDevice = "Test MIDI",
            ClipGuard = false,
            LowLatencyWasapi = false,
            AudioAutoMode = false,
            AudioBlockSizeMs = 7.5,
            OutputBufferMode = "manual",
            OutputBufferManualMs = 15,
            EngineCushionMode = "manual",
            EngineCushionManualMs = 20,
            ElasticRing = true,
        };
        var appSettingsJson = JsonSerializer.Serialize(appSettings);
        var appSettingsBack = JsonSerializer.Deserialize<AppSettings>(appSettingsJson) ?? new AppSettings();
        Check("settings persistence",
            appSettingsBack.Theme == "light" &&
            appSettingsBack.UseGpu &&
            !appSettingsBack.IsolateVst3 &&
            appSettingsBack.TranslationProvider == "opus" &&
            !appSettingsBack.ConnectorAnimation &&
            appSettingsBack.MinimizeToTray &&
            appSettingsBack.TrayPromptShown &&
            !appSettingsBack.ShowWelcomeScreen &&
            appSettingsBack.AggressiveOutputCushion &&
            appSettingsBack.MidiEnabled &&
            appSettingsBack.MidiInputDevice == "Test MIDI" &&
            !appSettingsBack.ClipGuard &&
            !appSettingsBack.LowLatencyWasapi &&
            !appSettingsBack.AudioAutoMode &&
            Math.Abs(appSettingsBack.AudioBlockSizeMs - 7.5) < 0.001 &&
            appSettingsBack.OutputBufferMode == "manual" &&
            appSettingsBack.OutputBufferManualMs == 15 &&
            appSettingsBack.EngineCushionMode == "manual" &&
            appSettingsBack.EngineCushionManualMs == 20 &&
            appSettingsBack.ElasticRing);

        var settingsDto = new SettingsDto
        {
            Theme = "light",
            UseGpu = true,
            IsolateVst3 = false,
            TranslationProvider = "opus",
            ConnectorAnimation = false,
            MinimizeToTray = true,
            ShowWelcomeScreen = false,
            GpuAvailable = true,
            GpuDeviceName = "Test GPU",
            AggressiveOutputCushion = true,
            MidiEnabled = true,
            MidiInputDevice = "Test MIDI",
            ClipGuard = false,
            LowLatencyWasapi = false,
            AudioAutoMode = false,
            AudioBlockSizeMs = 7.5,
            OutputBufferMode = "manual",
            OutputBufferManualMs = 15,
            EngineCushionMode = "manual",
            EngineCushionManualMs = 20,
            ElasticRing = true,
        };
        var settingsJson = JsonSerializer.Serialize(settingsDto, _json);
        var settingsBack = JsonSerializer.Deserialize<SettingsDto>(settingsJson, _json) ?? new SettingsDto();
        Check("settings bridge contract",
            settingsBack.Theme == "light" &&
            settingsBack.UseGpu &&
            !settingsBack.IsolateVst3 &&
            settingsBack.TranslationProvider == "opus" &&
            !settingsBack.ConnectorAnimation &&
            settingsBack.MinimizeToTray &&
            !settingsBack.ShowWelcomeScreen &&
            settingsBack.GpuAvailable &&
            settingsBack.GpuDeviceName == "Test GPU" &&
            settingsBack.AggressiveOutputCushion &&
            settingsBack.MidiEnabled &&
            settingsBack.MidiInputDevice == "Test MIDI" &&
            !settingsBack.ClipGuard &&
            !settingsBack.LowLatencyWasapi &&
            !settingsBack.AudioAutoMode &&
            Math.Abs(settingsBack.AudioBlockSizeMs - 7.5) < 0.001 &&
            settingsBack.OutputBufferMode == "manual" &&
            settingsBack.OutputBufferManualMs == 15 &&
            settingsBack.EngineCushionMode == "manual" &&
            settingsBack.EngineCushionManualMs == 20 &&
            settingsBack.ElasticRing);

        var midiModifier = new ModifierNode("MIDI test");
        var midiGain = new GainProcessor();
        midiModifier.Add(midiGain);
        var midiEntry = RegisterModifier(midiModifier, 0, 0);
        _midiLearnTarget = new MidiLearnTarget(midiEntry.Id, 0, 0);
        ApplyMidiMessage(new MidiInputEvent(0, MidiInputKind.ControlChange, 7, 127));
        bool midiBound = _midiBindings.Any(binding =>
            binding.NodeId == midiEntry.Id && binding.Kind == MidiInputKind.ControlChange && binding.Number == 7);
        ApplyMidiMessage(new MidiInputEvent(0, MidiInputKind.ControlChange, 7, 0));
        bool midiApplied = midiGain.Gain < 0.01f;
        _midiBindings.Clear();
        _midiLearnTarget = null;
        RemoveEntryInternal(midiEntry.Id);
        Check("MIDI learn and CC control", midiBound && midiApplied);

        bool previousCushion = OutputNode.AggressiveCushion;
        using (var cushionNode = OutputNode.CreateForTest(static (_, _) => { }))
        {
            OutputNode.AggressiveCushion = false;
            double safeCushion = cushionNode.TargetCushionMs;
            OutputNode.AggressiveCushion = true;
            double aggressiveCushion = cushionNode.TargetCushionMs;
            Check("aggressive cushion target", aggressiveCushion < safeCushion &&
                                                aggressiveCushion >= 3);
        }

        OutputNode.AggressiveCushion = previousCushion;

        var catalogJson = JsonSerializer.Serialize(new CatalogDto
        {
            PerAppCaptureAvailable = false,
            PerAppCaptureMessage = "Use system audio",
        }, _json);
        Check("per-app capability contract",
            catalogJson.Contains("\"perAppCaptureAvailable\":false", StringComparison.Ordinal) &&
            SourceNode.SupportsApplicationCapture == ProcessLoopbackCapture.IsSupported);

        Check("web origin", IsTrustedUiSource("https://ui.ruse/index.html") &&
                            !IsTrustedUiSource("https://example.com/") &&
                            !IsTrustedUiSource("http://ui.ruse/index.html"));

        Check("model checksums",
            TranscriptionModels.All.All(model => model.Sha256 is { Length: 64 }) &&
            TranslationModels.All.All(model => model.Sha256 is { Length: 64 }) &&
            ModelIntegrity.NormalizeSha256(
                "W/\"921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f\"") ==
            "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f" &&
            ModelIntegrity.NormalizeSha256("not-a-hash") == null);

        var telemetryJson = JsonSerializer.Serialize(new NodeTelemetryDto { UsesGpu = false }, _json);
        Check("gpu telemetry contract", telemetryJson.Contains("\"usesGpu\":false", StringComparison.Ordinal));

        var diagnosticsJson = JsonSerializer.Serialize(new DiagnosticsDto { OutputUnderruns = 2 }, _json);
        Check("underrun diagnostics contract", diagnosticsJson.Contains("\"outputUnderruns\":2", StringComparison.Ordinal));

        var formatJson = JsonSerializer.Serialize(new NodeDto { SampleRate = 44100, BitsPerSample = 16 }, _json);
        Check("node audio format contract",
            formatJson.Contains("\"sampleRate\":44100", StringComparison.Ordinal) &&
            formatJson.Contains("\"bitsPerSample\":16", StringComparison.Ordinal));

        var sendJson = JsonSerializer.Serialize(new LinkDto { Gain = 0.5f, Enabled = false }, _json);
        Check("send contract",
            sendJson.Contains("\"gain\":0.5", StringComparison.Ordinal) &&
            sendJson.Contains("\"enabled\":false", StringComparison.Ordinal));

        Check("send gain audio", LatencySelfTest.TestSendGain());
        Check("output mix observer", LatencySelfTest.TestOutputMixObserver());
        Check("native passthrough", LatencySelfTest.TestNativePassthrough());
        Check("native passthrough mono", LatencySelfTest.TestNativePassthroughMono());
        Check("native recorder rate", LatencySelfTest.TestRecorderSampleRate());

        var vulkan = VulkanProbe.Query();
        Check("vulkan probe",
            vulkan.Available
                ? !string.IsNullOrWhiteSpace(vulkan.DeviceName)
                : !string.IsNullOrWhiteSpace(vulkan.Error));

        Check("gpu vram hints",
            TranscriptionModels.All.All(model => model.VramMb > 0) &&
            TranslationModels.All.All(model => model.VramMb > 0));

        bool autoRun = _graph.AutoRun;
        _graph.AutoRun = false;
        try
        {
            var tapSource = RegisterSource(
                new SourceNode("Tap test source", SourceKind.Application, 12345), 40, 480);
            var tapModifier = new ModifierNode("Tap test");
            var tap = new TranscriptionProcessor();
            tap.SetTapId(TranscriptionProcessor.NextTapId());
            tapModifier.Add(tap);
            _graph.AddModifier(tapModifier);
            _graph.ConnectSourceToModifier(tapSource.Source!, tapModifier);
            _graph.StartEngine();
            Check("standalone tap worker", _graph.TapWorkerCount == 1);

            tapSource.Source!.WriteTestSamples(tone);
            Thread.Sleep(200);
            int monitored = TranscriptionProcessor.ReadAvailable(tap.TapId, tapBytes);
            Check("standalone tap audio", monitored > 0);
            _graph.StopEngine();

            _graph.RemoveModifier(tapModifier);
            Check("tap worker cleanup", _graph.TapWorkerCount == 0);
            _graph.RemoveSource(tapSource.Source!);
            _nodes.Remove(tapSource.Id);
            TranscriptionProcessor.RemoveTap(tap.TapId);
        }
        finally
        {
            _graph.AutoRun = autoRun;
        }

        bool singlePassAutoRun = _graph.AutoRun;
        _graph.AutoRun = false;
        string singlePassPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ruse-single-pass-{Guid.NewGuid():N}.wav");
        try
        {
            var singleSource = new SourceNode("Single-pass source", SourceKind.Application, 12345);
            _graph.AddSource(singleSource);
            var counter = new CountingSharedProcessor();
            var singleModifier = new ModifierNode("Single-pass chain");
            singleModifier.Add(counter);
            var singleSpectrum = new SpectrumProcessor();
            singleSpectrum.SetTapId(SpectrumProcessor.NextTapId());
            singleModifier.Add(singleSpectrum);
            _graph.AddModifier(singleModifier);
            _graph.ConnectSourceToModifier(singleSource, singleModifier);
            var singleOutput = _graph.AddOutputForFile(singlePassPath, AudioFileFormat.Wav16);
            _graph.ConnectModifierToOutput(singleModifier, singleOutput);
            singleOutput.Start();
            _graph.StartEngine();

            const int blocks = 60;
            var block = new float[240 * 2];
            for (int part = 0; part < blocks; part++)
            {
                for (int frame = 0; frame < 240; frame++)
                {
                    float value = (float)(0.3 * Math.Sin(
                        2 * Math.PI * 440 * (part * 240 + frame) / 48000.0));
                    block[frame * 2] = value;
                    block[frame * 2 + 1] = value;
                }

                singleSource.WriteTestSamples(block);
                Thread.Sleep(5);
            }

            Thread.Sleep(120);
            _graph.StopEngine();
            singleOutput.Stop();

            // With a tap monitor this used to run the shared processor twice;
            // compare the calls with the blocks that actually reached the file.
            int renderedBlocks = Math.Max(1, (int)Math.Round(
                singleOutput.RecordedSeconds * 48000.0 / 240.0));
            Check($"shared processor runs once ({counter.Calls}/{renderedBlocks})",
                counter.Calls >= renderedBlocks - 8 && counter.Calls <= renderedBlocks + 8);
            Check("single-pass recording",
                singleOutput.RecordedSeconds > 0.15 && string.IsNullOrEmpty(singleOutput.RecordingError));

            _graph.RemoveOutput(singleOutput);
            _graph.RemoveModifier(singleModifier);
            _graph.RemoveSource(singleSource);
            SpectrumProcessor.RemoveTap(singleSpectrum.TapId);
        }
        finally
        {
            _graph.AutoRun = singlePassAutoRun;
            try
            {
                if (System.IO.File.Exists(singlePassPath))
                {
                    System.IO.File.Delete(singlePassPath);
                }
            }
            catch
            {
            }
        }

        bool warningAutoRun = _graph.AutoRun;
        _graph.AutoRun = false;
        try
        {
            var warningSource = RegisterSource(
                new SourceNode("Duplicate warning source", SourceKind.Application, 12345), 40, 640);
            var warningOutput = RegisterOutput(
                OutputNode.CreateForTest(static (_, _) => { }), 360, 640);
            warningSource.SourceEndpointId = warningOutput.Output!.DeviceId;
            warningSource.SourceEndpointName = "Test output";
            ConnectEntries(warningSource, warningOutput, publish: false);
            var warnings = BuildGraphWarnings();
            Check("duplicate warning", warnings.Any(item =>
                item.SourceId == warningSource.Id && item.OutputId == warningOutput.Id));
            warningSource.SourceEndpointId = "different-endpoint";
            Check("different device ignored", !BuildGraphWarnings().Any(item =>
                item.SourceId == warningSource.Id && item.OutputId == warningOutput.Id));
            RemoveEntryInternal(warningSource.Id);
            RemoveEntryInternal(warningOutput.Id);
        }
        finally
        {
            _graph.AutoRun = warningAutoRun;
        }

        void Check(string name, bool condition)
        {
            checks.Add($"{name}={(condition ? "ok" : "FAIL")}");
            pass &= condition;
        }

        string timelineWave = Path.Combine(Path.GetTempPath(), "ruse-timeline-selftest.wav");
        var timeline = new TimelineService();
        try
        {
            using (var writer = new WaveFileWriter(timelineWave, new WaveFormat(48000, 16, 2)))
            {
                var samples = new float[12000 * 2];
                for (int frame = 0; frame < 12000; frame++)
                {
                    float value = 0.25f * MathF.Sin(2f * MathF.PI * 440f * frame / 48000f);
                    samples[frame * 2] = value;
                    samples[frame * 2 + 1] = value;
                }

                writer.WriteSamples(samples, 0, samples.Length);
            }

            var timelineTrack = timeline.AddTrack("Self test");
            Check("timeline add track", timeline.Project.Tracks.Count == 1);
            Check("timeline add clip", timeline.AddClip(timelineTrack.Id, timelineWave, out _));
            var timelineClip = timeline.Project.Tracks[0].Clips[0];
            var timelineSource = new SourceNode("Timeline self test", SourceKind.Timeline);
            var timelineSubscription = timelineSource.Subscribe();
            timeline.AttachSource(timelineTrack.Id, timelineSource);
            timelineSource.Start();
            timeline.Play();
            timeline.RenderBlock(480);
            var timelineBlock = new float[480 * 2];
            int timelineRead = timelineSubscription.Read(timelineBlock);
            Check("timeline clip renders", timelineRead > 0 &&
                timelineBlock.Any(sample => Math.Abs(sample) > 0.001f));
            timeline.Stop();
            timelineSubscription.Dispose();
            timelineSource.Dispose();
            Check("timeline clip length", timelineClip.LengthSamples == 12000);
            Check("timeline clip fades", timeline.UpdateClip(timelineTrack.Id, timelineClip.Id,
                null, 6000, null, null, 1000, 1200));
            timelineClip = timeline.Project.Tracks[0].Clips[0];
            Check("timeline fade limits",
                timelineClip.FadeInSamples == 1000 && timelineClip.FadeOutSamples == 1200 &&
                timelineClip.LengthSamples == 6000);

            timeline.SetLoop(1000, 5000);
            timeline.Seek(6000);
            Check("timeline loop seek", timeline.PlayheadSample == 1000);
            timeline.SetLoop(5000, 1000);
            Check("timeline invalid loop clears",
                timeline.Project.LoopStartSample < 0 && timeline.Project.LoopEndSample < 0);

            var automationValues = new List<float>();
            timeline.ApplyAutomation = (_, _, _, value) => automationValues.Add(value);
            var lane = timeline.AddAutomationLane(timelineTrack.Id, "selftest-node", 0, 0, 0f);
            timeline.SetAutomationPoints(timelineTrack.Id, lane.Id, new[]
            {
                new TimelineAutomationPoint { Sample = 0, Value = 0f },
                new TimelineAutomationPoint { Sample = 4800, Value = 1f },
            });
            timeline.Seek(0);
            timeline.Play();
            for (int i = 0; i < 10; i++) timeline.RenderBlock(480);
            timeline.Stop();
            Check("timeline automation", automationValues.Count == 10 &&
                automationValues.First() == 0f && automationValues.Last() > 0.8f);

            timeline.MoveClip(timelineTrack.Id, timelineClip.Id, 2400);
            Check("timeline move clip", timeline.Project.Tracks[0].Clips[0].StartSample == 2400);
            Check("timeline undo", timeline.Undo() &&
                timeline.Project.Tracks[0].Clips[0].StartSample == 0);
            Check("timeline redo", timeline.Redo() &&
                timeline.Project.Tracks[0].Clips[0].StartSample == 2400);

            string bouncePath = Path.Combine(Path.GetTempPath(), "ruse-timeline-selftest-bounce.wav");
            Check("timeline bounce", timeline.BounceToWave(bouncePath, out _) &&
                File.Exists(bouncePath) && new FileInfo(bouncePath).Length > 44);
            try { File.Delete(bouncePath); } catch { }

            string stretchPath = Path.Combine(Path.GetTempPath(), "ruse-timeline-selftest-stretch.wav");
            Check("timeline stretch", timeline.StretchClip(timelineTrack.Id, timelineClip.Id,
                12000, stretchPath, out _));
            Check("timeline stretch length", timeline.Project.Tracks[0].Clips[0].LengthSamples == 12000 &&
                File.Exists(stretchPath));
            int crossings = 0;
            int measuredFrames = 0;
            float previous = 0f;
            using (var reader = new AudioFileReader(stretchPath))
            {
                var buffer = new float[reader.WaveFormat.SampleRate * 2];
                int read;
                int frameIndex = 0;
                while ((read = reader.Read(buffer.AsSpan())) > 0)
                {
                    for (int sample = 0; sample < read; sample += 2)
                    {
                        if (frameIndex >= 3000 && frameIndex < 9000)
                        {
                            if (previous <= 0f && buffer[sample] > 0f) crossings++;
                            previous = buffer[sample];
                            measuredFrames++;
                        }

                        frameIndex++;
                    }
                }
            }

            Check("timeline stretch pitch", measuredFrames > 5000 &&
                Math.Abs(crossings * (double)AudioEngineConfig.SampleRate / measuredFrames - 440.0) < 35.0);
            timeline.Undo();
            Check("timeline stretch undo", timeline.Project.Tracks[0].Clips[0].LengthSamples == 6000);
            try { File.Delete(stretchPath); } catch { }

            timeline.SetTrackGain(timelineTrack.Id, 0.5f);
            Check("timeline can undo", timeline.CanUndo);
            timeline.LoadProject(timeline.SnapshotProject());
            Check("timeline load clears history", !timeline.CanUndo && !timeline.CanRedo);

            string timelineJson = JsonSerializer.Serialize(timeline.SnapshotProject(), _json);
            var restoredTimeline = JsonSerializer.Deserialize<TimelineProject>(timelineJson, _json);
            Check("timeline persistence",
                restoredTimeline is { Tracks.Count: 1 } &&
                restoredTimeline.Tracks[0].Automation.Count == 1 &&
                restoredTimeline.Tracks[0].Clips.Count > 0);
            if (restoredTimeline != null)
            {
                timeline.LoadProject(restoredTimeline);
            }

            timeline.Seek(0);
            timeline.Play();
            var renderTask = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++) timeline.RenderBlock(480);
            });
            for (int i = 0; i < 12; i++)
            {
                var current = timeline.Project.Tracks[0].Clips.LastOrDefault();
                if (current != null) timeline.RemoveClip(timelineTrack.Id, current.Id);
                timeline.AddClip(timelineTrack.Id, timelineWave, out _);
            }

            renderTask.Wait();
            timeline.Stop();
            Check("timeline concurrent edits", true);

            var dropTrack = timeline.AddTrack("Drop test");
            Check("timeline add clip at", timeline.AddClip(dropTrack.Id, timelineWave, out _, 2400) &&
                timeline.Project.Tracks.First(track => track.Id == dropTrack.Id).Clips[0].StartSample == 2400);
            Check("timeline remove track", timeline.RemoveTrack(dropTrack.Id) &&
                timeline.Project.Tracks.All(track => track.Id != dropTrack.Id));
        }
        finally
        {
            timeline.Dispose();
            try { File.Delete(timelineWave); } catch { }
        }

        string sampleSource = Path.Combine(Path.GetTempPath(), "ruse-sample-selftest-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var writer = new WaveFileWriter(sampleSource, new WaveFormat(48000, 16, 2)))
            {
                var samples = new float[480 * 2];
                for (int frame = 0; frame < 480; frame++)
                {
                    float value = 0.1f * MathF.Sin(2f * MathF.PI * 440f * frame / 48000f);
                    samples[frame * 2] = value;
                    samples[frame * 2 + 1] = value;
                }

                writer.WriteSamples(samples, 0, samples.Length);
            }

            ImportSamples(new[] { sampleSource });
            RefreshSamples();
            string importedSample = Path.Combine(SampleDirectory(), Path.GetFileName(sampleSource));
            Check("samples import", File.Exists(importedSample));
            Check("samples catalog", _catalog.Samples.Any(item =>
                string.Equals(item.FilePath, importedSample, StringComparison.OrdinalIgnoreCase)));
            Check("recording default path", NextRecordingPath(AudioFileFormat.Wav24)
                .StartsWith(RecordingDirectory(), StringComparison.OrdinalIgnoreCase));
            Check("recording samples path", NextRecordingPath(AudioFileFormat.Wav24, true)
                .StartsWith(SampleDirectory(), StringComparison.OrdinalIgnoreCase));
            try { File.Delete(importedSample); } catch { }
            RefreshSamples();
        }
        finally
        {
            try { File.Delete(sampleSource); } catch { }
        }

        var synthSettings = new SynthSettings(
            Frequency: 99999f,
            LevelDb: 10f,
            Harmonics: 2f,
            CutoffHz: 1f,
            Resonance: 2f,
            StereoWidth: -1f).Clamped();
        Check("synth parameter limits",
            synthSettings.Frequency == 4000f && synthSettings.LevelDb == 0f &&
            synthSettings.Harmonics == 1f && synthSettings.CutoffHz == 60f &&
            synthSettings.Resonance == 1f && synthSettings.StereoWidth == 0f);

        var synthCapture = new SynthGeneratorCapture(new SynthSettings(
            Frequency: 220f, LevelDb: -18f, Harmonics: 0.2f, CutoffHz: 3000f));
        int synthBytes = 0;
        float synthPeak = 0f;
        var synthEvent = new ManualResetEventSlim(false);
        synthCapture.DataAvailable += (_, packet) =>
        {
            Interlocked.Add(ref synthBytes, packet.BytesRecorded);
            for (int offset = 0; offset + 3 < packet.BytesRecorded; offset += 4)
            {
                float value = Math.Abs(BitConverter.ToSingle(packet.Buffer, offset));
                if (value > synthPeak) synthPeak = value;
            }

            if (synthBytes >= 4800) synthEvent.Set();
        };
        synthCapture.Start();
        bool synthAudio = synthEvent.Wait(2000);
        synthCapture.Stop();
        synthCapture.Dispose();
        synthEvent.Dispose();
        Check("synth audio", synthAudio && synthPeak > 0.001f && float.IsFinite(synthPeak));

        var source = RegisterSource(
            new SourceNode("Web test source", SourceKind.Application, 12345), 40, 40);
        PushAddNode(source);
        Check("add", _nodes.Count == 1);
        Undo();
        Check("undo add", _nodes.Count == 0);
        Redo();
        Check("redo add", _nodes.Count == 1);

        var output = RegisterOutput(
            OutputNode.CreateForTest(static (_, _) => { }), 320, 40);
        PushAddNode(output);
        var link = ConnectEntries(source, output, publish: false);
        PushConnect(link);
        Check("connect", _links.Count == 1);
        Undo();
        Check("undo connect", _links.Count == 0 && _nodes.Count == 2);
        Redo();
        Check("redo connect", _links.Count == 1);

        var insertedNode = new ModifierNode("Inserted effect");
        insertedNode.Add(new GainProcessor());
        var inserted = RegisterModifier(insertedNode, 600, 40);
        bool insertedOk = InsertModifierIntoLink(inserted, _links[0].Id);
        Check("insert into cable", insertedOk && _nodes.Count == 3 && _links.Count == 2);
        var outputNode = output.Output!;
        _graph.RebuildRouteProcessors(outputNode);
        Check("effect active", outputNode.Inputs.FirstOrDefault()?.Processors.Length == 1);
        insertedNode.Enabled = false;
        _graph.RebuildRouteProcessors(outputNode);
        Check("effect bypassed", outputNode.Inputs.FirstOrDefault()?.Processors.Length == 0);
        insertedNode.Enabled = true;
        _graph.RebuildRouteProcessors(outputNode);
        Undo();
        Check("undo insert into cable", _nodes.Count == 2 && _links.Count == 1);
        Redo();
        Check("redo insert into cable", _nodes.Count == 3 && _links.Count == 2);
        Undo();

        MoveNode(source.Id, 120, 160);
        Check("move", Math.Abs(_nodes[source.Id].X - 120) < 0.5);
        Undo();
        Check("undo move", Math.Abs(_nodes[source.Id].X - 40) < 0.5);
        Redo();
        Check("redo move", Math.Abs(_nodes[source.Id].X - 120) < 0.5);

        DuplicateNode(source.Id);
        Check("duplicate", _nodes.Count == 3);
        Undo();
        Check("undo duplicate", _nodes.Count == 2);
        Redo();
        Check("redo duplicate", _nodes.Count == 3);

        var chain = new ChainSaveDto
        {
            Name = "Web parity chain",
            Elements =
            {
                new ChainElementDto { TypeId = HighPassProcessor.Id },
                new ChainElementDto { TypeId = GainProcessor.Id },
            },
        };
        SaveChainFromTest(chain);
        Check("chain save", SubgraphLibrary.Chains.Any(item => item.Name == chain.Name));
        DeleteChain(chain.Name);
        Check("chain delete", SubgraphLibrary.Chains.All(item => item.Name != chain.Name));

        var included = new HashSet<string>(_nodes.Keys);
        var content = BuildPreset(included, 100, 200);
        var template = new FrameTemplate
        {
            Name = "Web parity frame",
            Width = 480,
            Height = 320,
            Content = content,
        };
        FrameTemplateLibrary.Store(template);
        FrameTemplateLibrary.Load();
        var loadedTemplate = FrameTemplateLibrary.Find(template.Name);
        Check("frame template save",
            loadedTemplate != null &&
            loadedTemplate.NodeCount == _nodes.Count &&
            loadedTemplate.Content.Sources.Count == content.Sources.Count &&
            loadedTemplate.Content.Sources.All(source => source.X >= 100 && source.Y >= 200));
        if (loadedTemplate != null)
        {
            DeleteFrameTemplate(loadedTemplate.Name);
        }

        Check("frame template delete", FrameTemplateLibrary.Find(template.Name) == null);

        var savedFrame = new FrameDto
        {
            Id = "self-test-frame",
            Name = "Web parity frame group",
            X = 0,
            Y = 0,
            Width = 800,
            Height = 800,
        };
        _frames.Add(savedFrame);
        SaveFrameTemplate(new FrameRefPayload { Id = savedFrame.Id });
        var savedTemplate = FrameTemplateLibrary.Find(savedFrame.Name);
        Check("frame template capture",
            savedTemplate != null &&
            savedTemplate.NodeCount == _nodes.Count &&
            savedTemplate.Content.Sources.All(source => source.X < savedFrame.Width));
        if (savedTemplate != null)
        {
            FrameTemplateLibrary.Remove(savedTemplate);
        }

        var insertTemplate = new FrameTemplate
        {
            Name = "Web parity insert",
            Width = 300,
            Height = 200,
            Content = new Preset
            {
                Modifiers =
                {
                    new PresetModifier
                    {
                        Title = "Template gain",
                        Processors = { new PresetProcessor { TypeId = GainProcessor.Id } },
                    },
                },
            },
        };
        FrameTemplateLibrary.Store(insertTemplate);
        int nodesBeforeInsert = _nodes.Count;
        InsertFrameTemplate(new TemplateInsertPayload { Id = insertTemplate.Name, X = 1200, Y = 1200 });
        Check("frame template insert",
            _nodes.Count == nodesBeforeInsert + 1 &&
            _frames.Any(frame => frame.Name == insertTemplate.Name &&
                                 Math.Abs(frame.X - 1200) < 0.5 &&
                                 Math.Abs(frame.Y - 1200) < 0.5));
        FrameTemplateLibrary.Remove(insertTemplate);

        var unsafePreset = new Preset
        {
            Outputs =
            {
                new PresetOutput
                {
                    IsFile = true,
                    FilePath = Path.Combine(Path.GetTempPath(), "ruse-untrusted-recorder.wav"),
                    RecordingMode = "On",
                    AutoSave = true,
                },
            },
        };
        ApplyPreset(unsafePreset, autoRun: false);
        var importedRecorder = _nodes.Values
            .Select(entry => entry.Output)
            .FirstOrDefault(output => output?.IsFileRecorder == true);
        Check("imported recorder disarmed",
            importedRecorder != null &&
            string.IsNullOrEmpty(importedRecorder.RecordingPath) &&
            importedRecorder.RecorderMode == RecorderMode.Off &&
            !importedRecorder.AutoSave &&
            !importedRecorder.AutoStart);

        var oversizedHeader = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(oversizedHeader, Vst3HostProtocol.CommandProcess);
        BinaryPrimitives.WriteInt32LittleEndian(oversizedHeader.AsSpan(4), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(oversizedHeader.AsSpan(8), int.MaxValue);
        bool oversizedRejected = false;
        try
        {
            using var oversizedStream = new MemoryStream(oversizedHeader);
            Vst3HostProtocol.ReadAudio(oversizedStream, new float[8], out _, out _, out _);
        }
        catch (InvalidDataException)
        {
            oversizedRejected = true;
        }

        Check("vst3 block bounds", oversizedRejected);

        var eqNode = new ModifierNode("EQ test");
        eqNode.Add(new GraphicEqualizerProcessor());
        var eqEntry = RegisterModifier(eqNode, 1400, 1400);
        var eqDto = CreateNodeDto(eqEntry);
        Check("graphic eq flag", eqDto.IsGraphicEq && eqDto.Parameters.Count == 10);

        bool bridgeAutoRun = _graph.AutoRun;
        _graph.AutoRun = false;
        try
        {
            var bridgeSource = RegisterSource(
                new SourceNode("Bridge test source", SourceKind.Application, 12345), 40, 1400);
            var bridgeModifierNode = new ModifierNode("Bridge test effect");
            bridgeModifierNode.Add(new GainProcessor());
            var bridgeModifier = RegisterModifier(bridgeModifierNode, 240, 1400);
            var bridgeOutput = RegisterOutput(
                OutputNode.CreateForTest(static (_, _) => { }), 440, 1400);
            ConnectEntries(bridgeSource, bridgeModifier);
            ConnectEntries(bridgeModifier, bridgeOutput);

            RemoveNode(bridgeModifier.Id);
            Check("remove effect bridges chain",
                !_nodes.ContainsKey(bridgeModifier.Id) &&
                _links.Any(link => link.From == bridgeSource.Id && link.To == bridgeOutput.Id));

            Undo();
            Check("undo remove effect",
                _nodes.ContainsKey(bridgeModifier.Id) &&
                _links.Any(link => link.From == bridgeSource.Id && link.To == bridgeModifier.Id) &&
                _links.Any(link => link.From == bridgeModifier.Id && link.To == bridgeOutput.Id) &&
                !_links.Any(link => link.From == bridgeSource.Id && link.To == bridgeOutput.Id));

            Redo();
            Check("redo remove effect",
                !_nodes.ContainsKey(bridgeModifier.Id) &&
                _links.Any(link => link.From == bridgeSource.Id && link.To == bridgeOutput.Id));
            Undo();

            var sendLink = _links.First(link => link.Kind == "modifier-output");
            SetLinkGain(sendLink.Id, 0.5f);
            Check("send gain applied",
                _nodes[bridgeModifier.Id].Modifier!.OutputGains.TryGetValue(bridgeOutput.Output!, out float sendGain) &&
                Math.Abs(sendGain - 0.5f) < 1e-6);

            SetLinkEnabled(sendLink.Id, false);
            Check("send disabled",
                Math.Abs(_nodes[bridgeModifier.Id].Modifier!.OutputGains[bridgeOutput.Output!]) < 1e-6);
            Undo();
            Undo();
        }
        finally
        {
            _graph.AutoRun = bridgeAutoRun;
        }

        ClearGraph();
        ClearHistory();
        return $"web parity: {string.Join(", ", checks)} -> {(pass ? "PASS" : "FAIL")}";
    }

    private void SaveChainFromTest(ChainSaveDto chain)
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(chain, _json);
        SaveChain(new BridgeMessage { Type = "chain-save", Payload = payload });
    }

    private sealed class CountingSharedProcessor : IAudioProcessor, ISharedProcessor
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public string TypeId => "test-shared";
        public string DisplayName => "Test shared";

        public void Process(Span<float> samples, int frames) =>
            Interlocked.Increment(ref _calls);

        public void Reset()
        {
        }
    }
}
