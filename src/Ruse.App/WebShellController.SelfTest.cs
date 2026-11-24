using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using NAudio.Midi;
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

        const string groupTestKey = "selftest:group";
        MixerGroupBus.SetGain(groupTestKey, 0.5f);
        MixerGroupBus.SetMuted(groupTestKey, true);
        MixerGroupBus.SetSolo(groupTestKey, true);
        Check("output wave buffer", LatencySelfTest.TestOutputWaveBuffer());
        Check("graph swap under load", LatencySelfTest.TestGraphSwapUnderLoad());
        Check("modifier fault recovery", LatencySelfTest.TestModifierFaultRecovery());
        Check("network packet reorder", LatencySelfTest.TestNetworkPacketReorder());
        Check("mixer node pan/mute/solo/crossfade/auto gain/width", LatencySelfTest.TestMixerNode());
        Check("mixer input delay", LatencySelfTest.TestMixerInputDelay());
        Check("output resampler zero delay", LatencySelfTest.TestOutputDriftResampler());
        Check("variable delay line", LatencySelfTest.TestVariableDelayLine());
        Check("mixer preset round-trip", LatencySelfTest.TestMixerPresetRoundTrip());
        Check("timeline markers, time signature and clip copy", LatencySelfTest.TestTimelineMarkersAndCopy());
        Check("timeline normalize, reverse and consolidate", LatencySelfTest.TestTimelineClipProcessing());
        var mixerSnapshotSource = RegisterModifier(
            new ModifierNode("Mixer snapshot")
            {
                IsMixer = true,
                MixerWidth = 0.25f,
                MixerAutoGain = true,
                MixerCrossfade = 0.75f,
            },
            0, 0, flavor: "mixer", subtitle: "Mixer");
        var mixerSnapshot = CaptureNode(mixerSnapshotSource);
        RemoveEntryInternal(mixerSnapshotSource.Id);
        var mixerRestored = RestoreNodeSnapshot(mixerSnapshot);
        Check("mixer node history snapshot",
            mixerRestored?.Modifier is
            {
                IsMixer: true,
                MixerAutoGain: true,
            } mixerRestoredModifier &&
            Math.Abs(mixerRestoredModifier.MixerWidth - 0.25f) < 1e-6f &&
            Math.Abs(mixerRestoredModifier.MixerCrossfade - 0.75f) < 1e-6f);
        if (mixerRestored != null)
        {
            RemoveEntryInternal(mixerRestored.Id);
        }

        var mixerSourceEntry = RegisterSource(new SourceNode("Mixer history source", SourceKind.Tone), 0, 0);
        var mixerEntry = AddMixer(0, 0);
        var mixerLink = ConnectEntries(mixerSourceEntry, mixerEntry);
        var linkIndexBefore = GetLinkIndex();
        Check("link index cache reuse", ReferenceEquals(linkIndexBefore, GetLinkIndex()));
        Check("link index includes connected input",
            linkIndexBefore.Incoming.TryGetValue(mixerEntry.Id, out var indexedInputs) &&
            indexedInputs.Any(link => link.Id == mixerLink.Id));
        mixerLink.Gain = 0.6f;
        mixerLink.Pan = 0.4f;
        mixerLink.DelayMs = 12f;
        mixerLink.Solo = true;
        mixerLink.BusB = true;
        mixerLink.Inverted = true;
        SyncMixerInput(mixerLink);
        var mixerLinkSnapshot = CloneLink(mixerLink);
        RemoveLinkInternal(mixerLink);
        var linkIndexAfterRemove = GetLinkIndex();
        Check("link index invalidates on remove",
            !ReferenceEquals(linkIndexBefore, linkIndexAfterRemove) &&
            (!linkIndexAfterRemove.Incoming.TryGetValue(mixerEntry.Id, out var removedInputs) ||
             removedInputs.All(link => link.Id != mixerLink.Id)));
        RestoreLink(mixerLinkSnapshot);
        var restoredMixerLink = _links.FirstOrDefault(link => link.Id == mixerLinkSnapshot.Id);
        Check("mixer link undo restores input settings",
            restoredMixerLink is { Pan: 0.4f, DelayMs: 12f, Solo: true, BusB: true, Inverted: true } &&
            mixerEntry.Modifier?.MixerSourceInputs.TryGetValue(mixerSourceEntry.Source!, out var restoredSettings) == true &&
            Math.Abs(restoredSettings.Pan - 0.4f) < 1e-6f &&
            Math.Abs(restoredSettings.DelayMs - 12f) < 1e-6f &&
            restoredSettings.Solo && restoredSettings.BusB && restoredSettings.Inverted);
        if (restoredMixerLink != null)
        {
            RemoveLinkInternal(restoredMixerLink);
        }

        RemoveEntryInternal(mixerSourceEntry.Id);
        RemoveEntryInternal(mixerEntry.Id);
        Check("mixer group solo audio", LatencySelfTest.TestMixerGroupSolo());
        Check("mixer output group solo", LatencySelfTest.TestMixerOutputGroupSolo());
        Check("mixer group bus",
            Math.Abs(MixerGroupBus.Gain(groupTestKey) - 0.5f) < 1e-6f &&
            MixerGroupBus.Muted(groupTestKey) && MixerGroupBus.Solo(groupTestKey));
        MixerGroupBus.Configure(null);

        Check("non-finite telemetry", Meter(float.NaN) == 0 &&
                                      Meter(float.PositiveInfinity) == 0 &&
                                      Finite(double.NegativeInfinity) == 0);

        var reusedMeter = new NodeTelemetryDto
        {
            Level = 1,
            Clipping = true,
            Spectrum = new[] { 1f, 2f },
            NoteName = "A4",
        };
        reusedMeter.Reset();
        Check("telemetry dto reset",
            reusedMeter.Level == 0 &&
            !reusedMeter.Clipping &&
            reusedMeter.Spectrum == null &&
            reusedMeter.NoteName == null &&
            Math.Abs(reusedMeter.MeterDbLeft + 60f) < 1e-6f);

        string nodeStateJson = JsonSerializer.Serialize(new NodeStateDto
        {
            Id = "node",
            Volume = 0.5f,
            Muted = true,
            GroupGain = 0.75f,
            GroupMuted = false,
            GroupSolo = true,
            Send = 0.25f,
            Parallel = true,
            Color = "",
            Accent = "#4F8DFF",
        }, _json);
        Check("node-state delta contract",
            nodeStateJson.Contains("\"volume\":0.5", StringComparison.Ordinal) &&
            nodeStateJson.Contains("\"muted\":true", StringComparison.Ordinal) &&
            nodeStateJson.Contains("\"groupGain\":0.75", StringComparison.Ordinal) &&
            nodeStateJson.Contains("\"groupMuted\":false", StringComparison.Ordinal) &&
            nodeStateJson.Contains("\"groupSolo\":true", StringComparison.Ordinal) &&
            nodeStateJson.Contains("\"send\":0.25", StringComparison.Ordinal) &&
            nodeStateJson.Contains("\"parallel\":true", StringComparison.Ordinal) &&
            !nodeStateJson.Contains("\"solo\"", StringComparison.Ordinal) &&
            !nodeStateJson.Contains("\"enabled\"", StringComparison.Ordinal));

        string mixerInputStateJson = JsonSerializer.Serialize(new MixerInputStateDto
        {
            MixerId = "mixer",
            Input = new MixerInputDto
            {
                LinkId = "link",
                Title = "Mic",
                Gain = 0.5f,
                Pan = -0.25f,
            },
        }, _json);
        Check("mixer-input-state delta contract",
            mixerInputStateJson.Contains("\"mixerId\":\"mixer\"", StringComparison.Ordinal) &&
            mixerInputStateJson.Contains("\"linkId\":\"link\"", StringComparison.Ordinal) &&
            mixerInputStateJson.Contains("\"input\":", StringComparison.Ordinal) &&
            !mixerInputStateJson.Contains("\"inputs\":", StringComparison.Ordinal));

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

        var clauses = SpeechText.Clauses(
            "Hello, world. This is a longer clause, with a comma, and another clause that should be short enough for streaming.");
        Check("speech clause splitting",
            clauses.Count >= 3 && clauses.All(clause => clause.Length <= 110));

        var cjkCues = SentenceSplitter.Split("你好。世界！", 0, 2);
        var cjkClauses = SpeechText.Clauses("你好。世界！");
        Check("CJK sentence segmentation",
            cjkCues.Count == 2 &&
            cjkCues[0].Text == "你好。" &&
            cjkCues[1].Text == "世界！" &&
            cjkClauses.Count == 2);

        Check("streaming tts catalog",
            TtsModels.All.Any(model => model.Family == "kokoro-sharp") &&
            TtsModels.All.Any(model => model.Family == "piper"));

        int resampledFrames = 0;
        var resampler = new MonoToStereo48k(24000, (_, frames) => resampledFrames += frames);
        resampler.Push(new float[240]);
        resampler.Flush();
        Check("tts pcm resample", resampledFrames >= 470);

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

        int segmentedTap = TranscriptionProcessor.NextTapId();
        TranscriptionProcessor.EnsureTap(segmentedTap);
        try
        {
            var segmentInput = new float[48000 * 3 * 2];
            for (int frame = 0; frame < 48000 * 2; frame++)
            {
                float sample = 0.05f * MathF.Sin(2f * MathF.PI * 180f * frame / 48000f);
                segmentInput[frame * 2] = sample;
                segmentInput[frame * 2 + 1] = sample;
            }

            TranscriptionProcessor.WriteTap(segmentedTap, segmentInput, 48000 * 3);
            using var segmenter = new VadSegmenter(segmentedTap, silero: null);
            bool segmentStarted = false;
            segmenter.SpeechStarted += () => segmentStarted = true;
            using var segmentCancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var segment = segmenter.ReadAsync(segmentCancel.Token).GetAwaiter().GetResult();
            Check("endpointed speech segment",
                segmentStarted && segment != null && segment.Samples.Length >= 16000 * 2);
        }
        finally
        {
            TranscriptionProcessor.RemoveTap(segmentedTap);
        }

        string vadSpeechPath = Path.Combine(AppContext.BaseDirectory, "Assets", "vad_speech.wav");
        float vadProbability = 0f;
        using (var silero = SileroVad.TryCreate())
        {
            if (silero != null && File.Exists(vadSpeechPath))
            {
                using var reader = new AudioFileReader(vadSpeechPath);
                ISampleProvider samples = reader;
                var frame = new float[SileroVad.WindowSamples];
                while (samples.Read(frame.AsSpan()) == frame.Length)
                {
                    vadProbability = Math.Max(vadProbability, silero.Process(frame));
                }
            }

            Check("silero vad speech", silero != null && vadProbability >= 0.5f);
        }

        var quietChunker = new SpeechChunker();
        var quietSamples = new float[16000 * 7];
        for (int i = 0; i < quietSamples.Length; i++)
        {
            quietSamples[i] = 0.00015f * MathF.Sin(2f * MathF.PI * 180f * i / 16000f);
        }

        bool quietCut = quietChunker.Add(quietSamples, out var quietChunk, out _);
        SpeechAudio.Normalize(quietChunk);
        Check("quiet speech chunking",
            quietCut && quietChunk.Length >= 16000 * 6 && quietChunk.Max(MathF.Abs) > 0.01f);

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
            int detectedRoot = SampleNoteDetector.DetectRootMidi(fileTestPath, out float detectedConfidence);
            Check("sample root note detection",
                detectedRoot == 57 && detectedConfidence >= 0.4f);
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

        var fileNoteSource = new SourceNode("File note test", SourceKind.File);
        fileNoteSource.SetFileNote(60, 72, 25);
        fileNoteSource.SetFileTapeMode(true);
        Check("file player note transpose",
            fileNoteSource.FileRootNote == 60 &&
            fileNoteSource.FileTargetNote == 72 &&
            Math.Abs(fileNoteSource.FilePitchCents - 25f) < 0.01f &&
            Math.Abs(fileNoteSource.FilePitchSemitones - 12.25f) < 0.01f &&
            fileNoteSource.FileTapeMode);
        fileNoteSource.Dispose();

        var appSettings = new AppSettings
        {
            Width = 1280,
            Height = 720,
            Left = 0,
            Top = 0,
            Theme = "light",
            UiScale = 1.25,
            MotionMode = "reduced",
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
        Check("support reports",
            BuildDiagnosticsReport().Contains("Ruse ", StringComparison.Ordinal) &&
            BuildSettingsReport().Contains("\"appVersion\"", StringComparison.Ordinal) &&
            BuildRoutesReport().Contains("Nodes:", StringComparison.Ordinal) &&
            BuildRoutesReport().Contains("Links:", StringComparison.Ordinal));
        Check("support privacy redaction",
            !SanitizeSupportText(
                @"C:\Users\alice\secret.wav 192.168.1.5 alice@example.com " +
                "00000000-0000-0000-0000-000000000000")
                .Contains("alice", StringComparison.OrdinalIgnoreCase) &&
            !SanitizeSupportText(@"C:\Users\alice\secret.wav").Contains("secret.wav", StringComparison.OrdinalIgnoreCase) &&
            !SanitizeSupportText("192.168.1.5").Contains("192.168", StringComparison.Ordinal) &&
            !SanitizeLogText("song.wav").Contains("song.wav", StringComparison.OrdinalIgnoreCase) &&
            SanitizeSupportText("Network local LAN IP: 192.168.1.5", "192.168.1.5")
                .Contains("192.168.1.5", StringComparison.Ordinal));
        string networkProbeId = "support-network-probe-" + Guid.NewGuid().ToString("N");
        var networkProbe = new SourceNode("Network link", SourceKind.Network);
        networkProbe.SetNetworkPort(NetworkPcm.DefaultPort);
        _nodes[networkProbeId] = new NodeEntry
        {
            Id = networkProbeId,
            Kind = "network",
            Flavor = "network",
            Title = "Network link",
            Subtitle = "Voice + text",
            Accent = "#4FD1C5",
            Source = networkProbe,
        };
        try
        {
            string networkReport = BuildNetworkReport();
            Check("support network LAN IP",
                networkReport.Contains("Local LAN IP:", StringComparison.Ordinal) &&
                networkReport.Contains(NetworkPcm.GetLocalAddress(), StringComparison.Ordinal) &&
                BuildDiagnosticsReport().Contains("Network local LAN IP:", StringComparison.Ordinal));
        }
        finally
        {
            _nodes.Remove(networkProbeId);
            networkProbe.Dispose();
        }

        string secretId = "support-text-probe-" + Guid.NewGuid().ToString("N");
        const string secretText = "private transcript and note text";
        _nodes[secretId] = new NodeEntry
        {
            Id = secretId,
            Kind = "text",
            Flavor = "note",
            Title = "Text note",
            Subtitle = "Notes and scripts",
            Accent = "#8FB7FF",
            TextContent = secretText,
            TextCommittedContent = secretText,
            TtsText = secretText,
        };
        try
        {
            Check("support content exclusion",
                !RedactSensitiveContent("log " + secretText).Contains(secretText, StringComparison.Ordinal));
        }
        finally
        {
            _nodes.Remove(secretId);
        }

        string supportTestDir = Path.Combine(Path.GetTempPath(), "ruse-support-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? supportBundle = ExportSupportBundle(
                supportTestDir,
                announce: false,
                description: "test description");
            bool supportBundleOk = false;
            if (supportBundle != null)
            {
                using var archive = ZipFile.OpenRead(supportBundle);
                var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
                supportBundleOk = names.Contains("README.txt") &&
                    names.Contains("description.txt") &&
                    names.Contains("diagnostics.txt") &&
                    names.Contains("settings.json") &&
                    names.Contains("devices.txt") &&
                    names.Contains("routes.txt") &&
                    names.Contains("log-tail.txt");
            }

            Check("support bundle", supportBundleOk);

            string minimalDir = supportTestDir + "-minimal";
            string? minimalBundle = ExportSupportBundle(
                minimalDir,
                announce: false,
                includeDiagnostics: false,
                description: "test description");
            bool minimalBundleOk = false;
            if (minimalBundle != null)
            {
                using var archive = ZipFile.OpenRead(minimalBundle);
                var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
                minimalBundleOk = names.Contains("README.txt") &&
                    names.Contains("version.txt") &&
                    names.Contains("description.txt") &&
                    !names.Contains("log-tail.txt") &&
                    !names.Contains("devices.txt") &&
                    !names.Contains("routes.txt");
            }

            Check("support bundle without diagnostics", minimalBundleOk);
        }
        finally
        {
            try
            {
                Directory.Delete(supportTestDir, recursive: true);
                Directory.Delete(supportTestDir + "-minimal", recursive: true);
            }
            catch
            {
            }
        }

        Check("settings persistence",
            appSettingsBack.Theme == "light" &&
            Math.Abs(appSettingsBack.UiScale - 1.25) < 0.001 &&
            appSettingsBack.MotionMode == "reduced" &&
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
            UiScale = 1.25,
            MotionMode = "full",
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
            Math.Abs(settingsBack.UiScale - 1.25) < 0.001 &&
            settingsBack.MotionMode == "full" &&
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

        Check("storage folder migration", TestStorageFolderMigration());

        Check("ui scale clamps",
            Math.Abs(ClampUiScale(5) - 1.5) < 0.001 &&
            Math.Abs(ClampUiScale(0.1) - 0.75) < 0.001 &&
            Math.Abs(ClampUiScale(double.NaN) - 1.0) < 0.001);

        if (_webView != null)
        {
            double previousScale = _settings.UiScale;
            _settings.UiScale = 1.25;
            ApplyUiScale();
            double appliedZoom = _webView.ZoomFactor;
            bool applied = Math.Abs(appliedZoom - 1.25) < 0.01;
            _settings.UiScale = 5;
            ApplyUiScale();
            double clampedZoom = _webView.ZoomFactor;
            bool clamped = Math.Abs(clampedZoom - 1.5) < 0.01;
            _settings.UiScale = previousScale;
            ApplyUiScale();
            Check("ui scale applies", applied && clamped);
        }

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

        var nodeMidiSource = RegisterSource(new SourceNode("MIDI node test", SourceKind.Tone), 0, 0);
        _midiLearnTarget = new MidiLearnTarget(nodeMidiSource.Id, -1, -1, "gain");
        ApplyMidiMessage(new MidiInputEvent(0, MidiInputKind.ControlChange, 11, 127));
        ApplyMidiMessage(new MidiInputEvent(0, MidiInputKind.ControlChange, 11, 127));
        bool nodeGainBound = nodeMidiSource.Source?.Gain > 1.49f;
        _midiLearnTarget = new MidiLearnTarget(nodeMidiSource.Id, -1, -1, "mute");
        ApplyMidiMessage(new MidiInputEvent(0, MidiInputKind.Note, 60, 127));
        ApplyMidiMessage(new MidiInputEvent(0, MidiInputKind.Note, 60, 127));
        bool nodeMuteBound = nodeMidiSource.Source?.Muted == true;
        _midiBindings.Clear();
        _midiLearnTarget = null;
        RemoveEntryInternal(nodeMidiSource.Id);
        Check("MIDI node gain and mute", nodeGainBound && nodeMuteBound);

        bool channelFilterOk =
            MidiRouting.AcceptsChannel(-1, 0) &&
            MidiRouting.AcceptsChannel(-1, 15) &&
            MidiRouting.AcceptsChannel(2, 2) &&
            !MidiRouting.AcceptsChannel(2, 1) &&
            !MidiRouting.AcceptsChannel(15, 14);
        var panicEvents = new List<(byte Status, byte Data1, byte Data2)>();
        MidiRouting.EnqueueAllNotesOff((status, data1, data2) =>
            panicEvents.Add((status, data1, data2)));
        bool panicOk = panicEvents.Count == 48;
        for (int channel = 0; channel < 16 && panicOk; channel++)
        {
            byte status = (byte)(0xB0 | channel);
            panicOk =
                panicEvents.Contains((status, (byte)120, (byte)0)) &&
                panicEvents.Contains((status, (byte)123, (byte)0)) &&
                panicEvents.Contains((status, (byte)64, (byte)0));
        }

        Check("MIDI channel and panic", channelFilterOk && panicOk);

        var midiSnapshotModifier = new ModifierNode("MIDI history test");
        midiSnapshotModifier.Add(new GainProcessor());
        var midiSnapshotEntry = RegisterModifier(midiSnapshotModifier, 0, 0);
        midiSnapshotEntry.Vst3MidiEnabled = true;
        midiSnapshotEntry.Vst3MidiConfigured = true;
        midiSnapshotEntry.Vst3MidiChannel = 5;
        midiSnapshotEntry.Vst3TransportEnabled = false;
        _midiBindings.Add(new MidiBindingEntry
        {
            NodeId = midiSnapshotEntry.Id,
            Target = "gain",
            Channel = 4,
            Kind = MidiInputKind.ControlChange,
            Number = 21,
            Min = 0f,
            Max = 1.5f,
        });
        var midiSnapshot = CaptureNode(midiSnapshotEntry);
        RemoveEntryInternal(midiSnapshotEntry.Id);
        var midiRestored = RestoreNodeSnapshot(midiSnapshot);
        string restoredMidiNodeId = midiRestored?.Id ?? "";
        bool midiHistoryOk = midiRestored != null &&
            midiRestored.Vst3MidiEnabled &&
            midiRestored.Vst3MidiConfigured &&
            midiRestored.Vst3MidiChannel == 5 &&
            !midiRestored.Vst3TransportEnabled &&
            _midiBindings.Any(binding =>
                binding.NodeId == restoredMidiNodeId &&
                binding.Target == "gain" &&
                binding.Channel == 4 &&
                binding.Number == 21);
        if (midiRestored != null)
        {
            RemoveEntryInternal(midiRestored.Id);
        }

        _midiBindings.Clear();
        Check("MIDI history snapshot", midiHistoryOk);

        static int RawMidi(int status, int data1, int data2) =>
            status | (data1 << 8) | (data2 << 16);
        var parsedOn = MidiInputManager.ParseChannelEvent(
            MidiEvent.FromRawMessage(RawMidi(0x90, 60, 100)));
        var parsedOff = MidiInputManager.ParseChannelEvent(
            MidiEvent.FromRawMessage(RawMidi(0x80, 60, 64)));
        var parsedZeroVelocity = MidiInputManager.ParseChannelEvent(
            MidiEvent.FromRawMessage(RawMidi(0x90, 60, 0)));
        var parsedCc = MidiInputManager.ParseChannelEvent(
            MidiEvent.FromRawMessage(RawMidi(0xB2, 7, 99)));
        Check("MIDI note-off parsing",
            parsedOn is { Kind: MidiInputKind.Note, Number: 60, Value: 100, Channel: 0 } &&
            parsedOff is { Kind: MidiInputKind.Note, Number: 60, Value: 0, Channel: 0 } &&
            parsedZeroVelocity is { Kind: MidiInputKind.Note, Number: 60, Value: 0, Channel: 0 } &&
            parsedCc is { Kind: MidiInputKind.ControlChange, Number: 7, Value: 99, Channel: 2 });

        using (var testMidi = new MidiInputManager())
        {
            int clockCount = 0;
            int noteCount = 0;
            int ccCount = 0;
            testMidi.ClockReceived += () => Interlocked.Increment(ref clockCount);
            testMidi.MessageReceived += message =>
            {
                if (message.Kind == MidiInputKind.Note && message.Value > 0)
                {
                    Interlocked.Increment(ref noteCount);
                }
                else if (message.Kind == MidiInputKind.ControlChange)
                {
                    Interlocked.Increment(ref ccCount);
                }
            };

            testMidi.OpenAsync(MidiInputManager.TestDeviceName).Wait(3000);
            Thread.Sleep(450);
            testMidi.CloseAsync().Wait(3000);
            Check("test MIDI device", clockCount > 5 && noteCount > 0 && ccCount > 0);
        }

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

        var latencyTelemetryJson = JsonSerializer.Serialize(new NodeTelemetryDto
        {
            InputLatencyMs = 1.2,
            OutputLatencyMs = 3.4,
        }, _json);
        Check("latency telemetry contract",
            latencyTelemetryJson.Contains("\"inputLatencyMs\":1.2", StringComparison.Ordinal) &&
            latencyTelemetryJson.Contains("\"outputLatencyMs\":3.4", StringComparison.Ordinal));

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
        Check("parallel send alignment", LatencySelfTest.TestParallelSendAlignment());
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
            _graph.StartEngine();
            singleOutput.Start();

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

            // A tap monitor used to run the shared processor twice. Use a
            // deterministic offline render so timing cannot affect the count.
            {
                var onceGraph = new AudioGraph { AutoRun = false };
                var onceSource = new SourceNode("Shared processor count", SourceKind.Application, 4242);
                var onceCounter = new CountingSharedProcessor();
                var onceModifier = new ModifierNode("Shared processor chain");
                onceModifier.Add(onceCounter);
                var onceSpectrum = new SpectrumProcessor();
                onceSpectrum.SetTapId(SpectrumProcessor.NextTapId());
                onceModifier.Add(onceSpectrum);
                onceGraph.AddSource(onceSource);
                onceGraph.AddModifier(onceModifier);
                var onceOutput = OutputNode.CreateForTest(static (_, _) => { });
                onceOutput.EnableGraphEngine();
                onceGraph.AddOutputNode(onceOutput);
                onceGraph.ConnectSourceToModifier(onceSource, onceModifier);
                onceGraph.ConnectModifierToOutput(onceModifier, onceOutput);
                var onceBlock = new float[240 * 2];
                Array.Fill(onceBlock, 0.1f);
                for (int i = 0; i < 60; i++)
                {
                    onceSource.WriteTestSamples(onceBlock);
                    onceGraph.RenderOfflineBlock();
                }

                Check($"shared processor runs once ({onceCounter.Calls}/60)", onceCounter.Calls == 60);
                onceGraph.Dispose();
                SpectrumProcessor.RemoveTap(onceSpectrum.TapId);
            }

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

        bool invalidNodeAutoRun = _graph.AutoRun;
        _graph.AutoRun = false;
        try
        {
            var invalidSource = RegisterSource(
                new SourceNode(
                    "Missing input device",
                    SourceKind.Device,
                    deviceId: "{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"),
                40,
                700);
            var skippedNames = new List<string>();
            var savedWithoutInvalid = BuildPreset(skipped: skippedNames);
            Check("invalid device node skipped on save",
                savedWithoutInvalid.Sources.All(source => source.Id != invalidSource.Id) &&
                skippedNames.Count == 1 &&
                skippedNames[0] == invalidSource.Title);
            var missingFile = RegisterSource(new SourceNode("Missing file", SourceKind.File), 40, 760);
            missingFile.Source!.SetFile(
                Path.Combine(Path.GetTempPath(), $"ruse-missing-{Guid.NewGuid():N}.wav"),
                loop: false);
            var savedWithMissingFile = BuildPreset();
            Check("missing file node is kept on save",
                savedWithMissingFile.Sources.Any(source => source.Id == missingFile.Id));
            RemoveEntryInternal(missingFile.Id);
            RemoveEntryInternal(invalidSource.Id);
        }
        finally
        {
            _graph.AutoRun = invalidNodeAutoRun;
        }

        bool freezeAutoRun = _graph.AutoRun;
        _graph.AutoRun = false;
        string freezeInput = Path.Combine(Path.GetTempPath(), $"ruse-freeze-in-{Guid.NewGuid():N}.wav");
        string freezeOutput = Path.Combine(Path.GetTempPath(), $"ruse-freeze-out-{Guid.NewGuid():N}.wav");
        string freezeSourceId = "";
        string freezeModifierId = "";
        string freezeOutputId = "";
        string freezeSecondOutputId = "";
        try
        {
            using (var writer = new WaveFileWriter(freezeInput, new WaveFormat(48000, 16, 2)))
            {
                var toneSamples = new float[9600 * 2];
                for (int frame = 0; frame < 9600; frame++)
                {
                    float value = 0.3f * MathF.Sin(2f * MathF.PI * 440f * frame / 48000f);
                    toneSamples[frame * 2] = value;
                    toneSamples[frame * 2 + 1] = value;
                }

                writer.WriteSamples(toneSamples, 0, toneSamples.Length);
            }

            var freezeSource = RegisterSource(new SourceNode("Freeze self test", SourceKind.File), 40, 900);
            freezeSourceId = freezeSource.Id;
            freezeSource.Source!.SetFile(freezeInput, false);
            freezeSource.Source.Gain = 0.5f;
            var freezeModifier = new ModifierNode("Freeze self test chain");
            freezeModifier.Add(new GainProcessor { Gain = 0.5f });
            var freezeModifierEntry = RegisterModifier(freezeModifier, 40, 1000);
            freezeModifierId = freezeModifierEntry.Id;
            var freezeTarget = RegisterOutput(OutputNode.CreateForTest(static (_, _) => { }), 300, 1000);
            freezeOutputId = freezeTarget.Id;
            ConnectEntries(freezeSource, freezeModifierEntry, publish: false);
            ConnectEntries(freezeModifierEntry, freezeTarget, publish: false);

            var freezeBounce = BuildSourceFreezeGraph(freezeSource.Id, out _, out string freezeError);
            bool frozen = false;
            try
            {
                frozen = freezeBounce != null && freezeBounce.Render(freezeOutput, out freezeError);
            }
            finally
            {
                freezeBounce?.Dispose();
            }

            float freezePeak = 0;
            using (var reader = new AudioFileReader(freezeOutput))
            {
                var readBuffer = new float[4096];
                int read;
                while ((read = reader.Read(readBuffer.AsSpan())) > 0)
                {
                    for (int sample = 0; sample < read; sample++)
                    {
                        freezePeak = Math.Max(freezePeak, Math.Abs(readBuffer[sample]));
                    }
                }
            }

            Check("source freeze gain", freezePeak is > 0.12f and < 0.18f);
            Check("source freeze render", frozen &&
                File.Exists(freezeOutput) &&
                new FileInfo(freezeOutput).Length > 1000);

            var freezeSecondTarget = RegisterOutput(OutputNode.CreateForTest(static (_, _) => { }), 300, 1100);
            freezeSecondOutputId = freezeSecondTarget.Id;
            ConnectEntries(freezeModifierEntry, freezeSecondTarget, publish: false);
            var sharedFreeze = BuildSourceFreezeGraph(freezeSource.Id, out _, out _);
            sharedFreeze?.Dispose();
            Check("source freeze dedicated route", sharedFreeze == null);
        }
        finally
        {
            if (_nodes.ContainsKey(freezeSourceId)) RemoveEntryInternal(freezeSourceId);
            if (_nodes.ContainsKey(freezeModifierId)) RemoveEntryInternal(freezeModifierId);
            if (_nodes.ContainsKey(freezeOutputId)) RemoveEntryInternal(freezeOutputId);
            if (_nodes.ContainsKey(freezeSecondOutputId)) RemoveEntryInternal(freezeSecondOutputId);
            _graph.AutoRun = freezeAutoRun;
            try { if (File.Exists(freezeInput)) File.Delete(freezeInput); } catch { }
            try { if (File.Exists(freezeOutput)) File.Delete(freezeOutput); } catch { }
        }

        string cleanupFrozenPath = Path.Combine(Path.GetTempPath(), $"ruse-frozen-cleanup-{Guid.NewGuid():N}.wav");
        string cleanupFrozenSourceId = "";
        try
        {
            File.WriteAllBytes(cleanupFrozenPath, new byte[4096]);
            FrozenProxyStore.Track(cleanupFrozenPath);
            var cleanupFrozenSource = RegisterSource(new SourceNode("Frozen cleanup", SourceKind.File), 40, 1200);
            cleanupFrozenSourceId = cleanupFrozenSource.Id;
            cleanupFrozenSource.Source!.SetFile(cleanupFrozenPath, false);
            var cleanupFrozenSnapshot = CaptureNode(cleanupFrozenSource);
            RemoveEntryInternal(cleanupFrozenSource.Id);
            bool moved = !File.Exists(cleanupFrozenPath);
            var restoredFrozenSource = RestoreNodeSnapshot(cleanupFrozenSnapshot);
            bool restored = restoredFrozenSource != null && File.Exists(cleanupFrozenPath);
            if (restoredFrozenSource != null) RemoveEntryInternal(restoredFrozenSource.Id);
            Check("frozen proxy cleanup", moved && restored);
        }
        finally
        {
            if (_nodes.ContainsKey(cleanupFrozenSourceId)) RemoveEntryInternal(cleanupFrozenSourceId);
            FrozenProxyStore.Forget(cleanupFrozenPath);
            try { if (File.Exists(cleanupFrozenPath)) File.Delete(cleanupFrozenPath); } catch { }
        }

        bool nativeAutoRun = _graph.AutoRun;
        _graph.AutoRun = false;
        string nativeSourceId = "";
        string nativeOutputId = "";
        string nativeSecondSourceId = "";
        try
        {
            var nativeSource = RegisterSource(new SourceNode("Native toggle", SourceKind.Tone), 40, 1400);
            nativeSourceId = nativeSource.Id;
            nativeSource.Source!.Start();
            var nativeOutput = RegisterOutput(OutputNode.CreateForTest(static (_, _) => { }), 300, 1400);
            nativeOutputId = nativeOutput.Id;
            var nativeLink = ConnectEntries(nativeSource, nativeOutput, publish: false);
            SetLinkNative(nativeLink.Id, true);
            bool nativeEnabled = nativeLink.Native && nativeOutput.Output!.IsNativePassthrough &&
                nativeOutput.Output.NativeSource != null;

            var nativeSecondSource = RegisterSource(new SourceNode("Native toggle 2", SourceKind.Tone), 40, 1500);
            nativeSecondSourceId = nativeSecondSource.Id;
            nativeSecondSource.Source!.Start();
            ConnectEntries(nativeSecondSource, nativeOutput, publish: false);
            bool nativeAutoDisabled = !nativeLink.Native && !nativeOutput.Output!.IsNativePassthrough &&
                nativeOutput.Output.NativeSource == null;
            SetLinkNative(nativeLink.Id, false);
            bool nativeDisabled = !nativeLink.Native && !nativeOutput.Output!.IsNativePassthrough &&
                nativeOutput.Output.NativeSource == null;
            Check("native passthrough auto-disables on second route",
                nativeEnabled && nativeAutoDisabled && nativeDisabled);

        var metronomeSource = new SourceNode("Metronome self test", SourceKind.Tone);
        metronomeSource.SetMetronome(120f, 4);
        metronomeSource.Start();
        bool metronomeRunning = metronomeSource.IsRunning;
        metronomeSource.Stop();
        metronomeSource.Dispose();
        Check("timeline metronome source", metronomeRunning);
        }
        finally
        {
            if (_nodes.ContainsKey(nativeSourceId)) RemoveEntryInternal(nativeSourceId);
            if (_nodes.ContainsKey(nativeSecondSourceId)) RemoveEntryInternal(nativeSecondSourceId);
            if (_nodes.ContainsKey(nativeOutputId)) RemoveEntryInternal(nativeOutputId);
            _graph.AutoRun = nativeAutoRun;
        }

        string gateGuardSourceId = "";
        string gateGuardMetronomeId = "";
        try
        {
            var gateGuardSource = RegisterSource(
                new SourceNode("Gate guard timeline", SourceKind.Timeline), 40, 1600);
            gateGuardSourceId = gateGuardSource.Id;
            gateGuardSource.Source!.TimelineGate = 0.75f;
            ApplyGeneratorGate(gateGuardSourceId, 0f);
            Check("generator gate ignores timeline sources",
                Math.Abs(gateGuardSource.Source.TimelineGate - 0.75f) < 1e-6f);

            var gateGuardMetronome = RegisterSource(
                new SourceNode("Gate guard metronome", SourceKind.Tone), 40, 1700);
            gateGuardMetronomeId = gateGuardMetronome.Id;
            gateGuardMetronome.Source!.SetMetronome(120f, 4);
            gateGuardMetronome.Source.TimelineGate = 1f;
            ApplyGeneratorGate(gateGuardMetronomeId, 0.25f);
            Check("generator gate applies to metronome nodes",
                Math.Abs(gateGuardMetronome.Source.TimelineGate - 0.25f) < 1e-6f);
        }
        finally
        {
            if (_nodes.ContainsKey(gateGuardSourceId)) RemoveEntryInternal(gateGuardSourceId);
            if (_nodes.ContainsKey(gateGuardMetronomeId)) RemoveEntryInternal(gateGuardMetronomeId);
        }

        var generatorLoop = new SourceNode("Generator loop test", SourceKind.Tone);
        generatorLoop.SetTone(440f, ToneWaveform.Sine, -18f);
        var generatorSamples = GeneratorLoopRenderer.Render(generatorLoop, 4800);
        Check("generator loop render", generatorSamples.Length == 9600 &&
            generatorSamples.Any(sample => Math.Abs(sample) > 0.001f));

        var sourceAutomation = SourceAutomationParameters(generatorLoop);
        sourceAutomation[0].Set(440f);
        Check("source automation parameters", sourceAutomation.Count >= 3 &&
            Math.Abs(generatorLoop.ToneFrequency - 440f) < 1f);
        generatorLoop.Dispose();

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
            Check("timeline rename track",
                timeline.RenameTrack(timelineTrack.Id, "Renamed track", out _) &&
                timeline.Project.Tracks[0].Name == "Renamed track");
            timeline.Undo();
            Check("timeline rename undo", timeline.Project.Tracks[0].Name == "Self test");
            timeline.Redo();
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

            {
                var isolatedTimeline = new TimelineService();
                var isolatedTrack = isolatedTimeline.AddTrack("Offline isolation");
                bool isolatedAdd = isolatedTimeline.AddClip(isolatedTrack.Id, timelineWave, out _);
                var isolatedClip = isolatedTimeline.Project.Tracks[0].Clips[0];
                var offlineSnapshot = isolatedTimeline.SnapshotProject(includePeaks: false);
                isolatedTimeline.MoveClip(isolatedTrack.Id, isolatedClip.Id, 4800);
                isolatedTimeline.SetTrackGain(isolatedTrack.Id, 0.25f);
                var offlineTimeline = new TimelineService();
                offlineTimeline.LoadProject(offlineSnapshot);
                Check("timeline offline snapshot isolation", isolatedAdd &&
                    offlineTimeline.Project.Tracks[0].Clips[0].StartSample == 0 &&
                    Math.Abs(offlineTimeline.Project.Tracks[0].Gain - 1f) < 0.001f);
                offlineTimeline.Dispose();
                isolatedTimeline.Dispose();
            }

            {
                var splitTimeline = new TimelineService();
                var splitTrack = splitTimeline.AddTrack("Split test");
                bool addSplit = splitTimeline.AddClip(splitTrack.Id, timelineWave, out _);
                var splitClip = splitTimeline.Project.Tracks[0].Clips[0];
                bool splitOk = splitTimeline.SplitClip(splitTrack.Id, splitClip.Id, 6000);
                var splitClips = splitTimeline.Project.Tracks[0].Clips;
                bool duplicateOk = splitTimeline.DuplicateClip(splitTrack.Id, splitClips[0].Id);
                Check("timeline split and duplicate", addSplit && splitOk && duplicateOk &&
                    splitClips.Count == 3 && splitClips[0].LengthSamples == 6000 &&
                    splitClips[1].SourceOffsetSamples == 6000 && splitClips[1].LengthSamples == 6000);
                bool crossfadeOk = splitTimeline.CrossfadeClip(splitTrack.Id, splitClips[2].Id);
                Check("timeline crossfade", crossfadeOk && splitClips[2].FadeOutSamples == 6000 &&
                    splitClips[1].FadeInSamples == 6000);

                var loopTimeline = new TimelineService();
                var loopTrack = loopTimeline.AddTrack("Loop test");
                bool loopAdd = loopTimeline.AddClip(loopTrack.Id, timelineWave, out _);
                var loopClip = loopTimeline.Project.Tracks[0].Clips[0];
                bool loopSet = loopTimeline.SetClipLoop(loopTrack.Id, loopClip.Id, true);
                bool loopLength = loopTimeline.UpdateClip(loopTrack.Id, loopClip.Id, null, 24000, null, null, null, null);
                Check("timeline generator loop length", loopAdd && loopSet && loopLength &&
                    loopClip.Loop && loopClip.LengthSamples == 24000);
                loopTimeline.Dispose();

                var gateTimeline = new TimelineService();
                var gateTrack = gateTimeline.AddGeneratorTrack("generator-test", "Generator");
                bool gateAdded = gateTimeline.AddGeneratorClip(gateTrack.Id, 0, 4800, "Generator", out _);
                float gateGain = -1;
                gateTimeline.ApplyGeneratorGate = (_, gain) => gateGain = gain;
                gateTimeline.Play();
                gateTimeline.RenderBlock(480);
                Check("timeline generator gate", gateAdded && gateGain > 0f);
                gateTimeline.Dispose();


                splitTimeline.Dispose();
            }


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

            {
                var graphBounce = new AudioGraph { AutoRun = false };
                var graphTimeline = new TimelineService();
                graphTimeline.LoadProject(timeline.SnapshotProject(includePeaks: false));
                var graphSource = new SourceNode("Timeline graph self test", SourceKind.Timeline);
                graphTimeline.AttachSource(timelineTrack.Id, graphSource);
                graphSource.Start();
                graphBounce.Timeline = graphTimeline;
                graphBounce.AddSource(graphSource);
                var graphOutput = OutputNode.CreateForTest(static (_, _) => { });
                graphOutput.EnableGraphEngine();
                graphBounce.AddOutputNode(graphOutput);
                graphBounce.Connect(graphSource, graphOutput, 1f);
                graphTimeline.Seek(0);
                graphTimeline.Play();
                var graphBlock = new float[AudioEngineConfig.BlockFrames * 2];
                double graphPeak = 0;
                for (int i = 0; i < 40; i++)
                {
                    graphBounce.RenderOfflineBlock();
                    int read = graphOutput.ReadOfflineBlock(graphBlock);
                    for (int sample = 0; sample < read; sample++)
                    {
                        graphPeak = Math.Max(graphPeak, Math.Abs(graphBlock[sample]));
                    }
                }

                Check("timeline graph bounce", graphPeak > 0.05);
                graphBounce.Dispose();
                graphTimeline.Dispose();
            }

            {
                var multiTimeline = new TimelineService();
                var trackA = multiTimeline.AddTrack("A");
                var trackB = multiTimeline.AddTrack("B");
                bool clipsOk = multiTimeline.AddClip(trackA.Id, timelineWave, out _) &&
                               multiTimeline.AddClip(trackB.Id, timelineWave, out _);
                var sourceA = new SourceNode("Multi A", SourceKind.Timeline);
                var sourceB = new SourceNode("Multi B", SourceKind.Timeline);
                multiTimeline.AttachSource(trackA.Id, sourceA);
                multiTimeline.AttachSource(trackB.Id, sourceB);
                sourceA.Start();
                sourceB.Start();

                var multiGraph = new AudioGraph { AutoRun = false };
                multiGraph.Timeline = multiTimeline;
                multiGraph.AddSource(sourceA);
                multiGraph.AddSource(sourceB);

                var outA = OutputNode.CreateForTest(static (_, _) => { });
                var outB = OutputNode.CreateForTest(static (_, _) => { });
                outA.EnableGraphEngine();
                outB.EnableGraphEngine();
                multiGraph.AddOutputNode(outA);
                multiGraph.AddOutputNode(outB);

                var gain = new GainProcessor();
                gain.Parameters.First(p => p.Name == "Gain").Set(0.5f);
                var gainNode = new ModifierNode("Gain send");
                gainNode.Add(gain);
                multiGraph.AddModifier(gainNode);
                multiGraph.ConnectSourceToModifier(sourceA, gainNode);
                multiGraph.ConnectModifierToOutput(gainNode, outA, 0.8f);
                multiGraph.Connect(sourceA, outA, 0.8f);

                var ducker = new SidechainDuckerProcessor();
                var duckNode = new ModifierNode("Keyed send");
                duckNode.Add(ducker);
                multiGraph.AddModifier(duckNode);
                multiGraph.ConnectSourceToModifier(sourceB, duckNode);
                multiGraph.ConnectSourceToModifierSidechain(sourceA, duckNode);
                multiGraph.ConnectModifierToOutput(duckNode, outA, 1f);
                multiGraph.Connect(sourceB, outB, 1f);

                multiTimeline.Seek(0);
                multiTimeline.Play();
                var multiBlock = new float[AudioEngineConfig.BlockFrames * 2];
                var multiBlockB = new float[AudioEngineConfig.BlockFrames * 2];
                double peakA = 0;
                double peakB = 0;
                for (int i = 0; i < 40; i++)
                {
                    multiGraph.RenderOfflineBlock();
                    int readA = outA.ReadOfflineBlock(multiBlock);
                    int readB = outB.ReadOfflineBlock(multiBlockB);
                    for (int sample = 0; sample < readA; sample++) peakA = Math.Max(peakA, Math.Abs(multiBlock[sample]));
                    for (int sample = 0; sample < readB; sample++) peakB = Math.Max(peakB, Math.Abs(multiBlockB[sample]));
                }

                Check("timeline multi-route bounce", clipsOk && peakA > 0.05 && peakB > 0.05 && peakA > peakB);
                multiTimeline.Stop();
                multiGraph.Dispose();
                multiTimeline.Dispose();
            }

            {
                var pitchTimeline = new TimelineService();
                var pitchTrack = pitchTimeline.AddTrack("Pitch test");
                bool pitchClipOk = pitchTimeline.AddClip(pitchTrack.Id, timelineWave, out _);
                var pitchClip = pitchTimeline.Project.Tracks[0].Clips[0];
                bool pitchSet = pitchTimeline.UpdateClip(pitchTrack.Id, pitchClip.Id,
                    null, null, null, null, null, null, 12f);
                var pitchSource = new SourceNode("Pitch self test", SourceKind.Timeline);
                var pitchSubscription = pitchSource.Subscribe();
                pitchTimeline.AttachSource(pitchTrack.Id, pitchSource);
                pitchSource.Start();
                pitchTimeline.Play();
                pitchTimeline.RenderBlock(480);
                var pitchBlock = new float[480 * 2];
                int pitchRead = pitchSubscription.Read(pitchBlock);
                int pitchCrossings = 0;
                for (int i = 2; i < pitchRead; i += 2)
                {
                    if ((pitchBlock[i - 2] <= 0f && pitchBlock[i] > 0f) ||
                        (pitchBlock[i - 2] >= 0f && pitchBlock[i] < 0f))
                    {
                        pitchCrossings++;
                    }
                }

                Check("timeline clip pitch", pitchClipOk && pitchSet &&
                    Math.Abs(pitchClip.PitchSemitones - 12f) < 0.001f && pitchCrossings >= 14);

                pitchTimeline.Seek(0);
                pitchTimeline.Play();
                bool pitchAudibleLate = false;
                for (int block = 0; block < 24; block++)
                {
                    pitchTimeline.RenderBlock(480);
                    int read = pitchSubscription.Read(pitchBlock);
                    if (block == 23 && read > 0)
                    {
                        for (int sample = 0; sample < read; sample++)
                        {
                            if (Math.Abs(pitchBlock[sample]) > 0.001f)
                            {
                                pitchAudibleLate = true;
                                break;
                            }
                        }
                    }
                }

                Check("timeline clip pitch preserves duration", pitchAudibleLate);

                var pitchLane = pitchTimeline.AddAutomationLane(
                    pitchTrack.Id, $"clip:{pitchClip.Id}", -1, 0, 0f);
                pitchTimeline.SetAutomationPoints(pitchTrack.Id, pitchLane.Id, new[]
                {
                    new TimelineAutomationPoint { Sample = 0, Value = 0f },
                    new TimelineAutomationPoint { Sample = 480, Value = 12f },
                });
                pitchTimeline.Seek(0);
                pitchTimeline.Play();
                pitchTimeline.RenderBlock(480);
                bool pitchAtStart = pitchClip.PitchAutomationSemitones.HasValue &&
                    Math.Abs(pitchClip.PitchAutomationSemitones.Value) < 0.01f;
                pitchTimeline.RenderBlock(480);
                bool pitchAtEnd = pitchClip.PitchAutomationSemitones.HasValue &&
                    Math.Abs(pitchClip.PitchAutomationSemitones.Value - 12f) < 0.01f;
                Check("timeline clip pitch automation", pitchAtStart && pitchAtEnd &&
                    Math.Abs(pitchClip.PitchSemitones - 12f) < 0.001f);
                pitchTimeline.RemoveAutomationLane(pitchTrack.Id, pitchLane.Id);
                Check("timeline clip pitch automation clears override",
                    pitchClip.PitchAutomationSemitones == null &&
                    Math.Abs(pitchClip.PitchSemitones - 12f) < 0.001f);
                pitchSubscription.Dispose();
                pitchSource.Dispose();
                pitchTimeline.Dispose();
            }

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
            RuseFolders.EnsureAll();
            Check("user content folders", new[]
            {
                RuseFolders.Presets,
                RuseFolders.Recordings,
                RuseFolders.Samples,
                RuseFolders.Exports,
                RuseFolders.Notes,
            }.All(Directory.Exists));
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

        var autoModifierNode = new ModifierNode("Auto connect effect");
        autoModifierNode.Add(new GainProcessor());
        var autoModifier = RegisterModifier(autoModifierNode, 600, 40);
        var autoLink = TryAutoConnect(autoModifier, output);
        bool autoLinked = autoLink != null &&
            _links.Any(item => item.From == autoModifier.Id && item.To == output.Id);
        bool autoDuplicateBlocked =
            TryAutoConnect(autoModifier, output) == null &&
            TryAutoConnect(source, output) == null;
        RemoveEntryInternal(autoModifier.Id);
        bool autoCleaned = _links.Count == 1 && _nodes.Count == 2 &&
            _links.All(item => item.From != autoModifier.Id && item.To != autoModifier.Id);
        Check("auto connect", autoLinked && autoDuplicateBlocked && autoCleaned);

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

        var legacyTimelinePreset = new Preset
        {
            Sources =
            {
                new PresetSource
                {
                    Id = "preset-timeline-source",
                    Title = "Preset timeline",
                    Kind = "Timeline",
                },
            },
            Timeline = new TimelineProject
            {
                Tracks =
                {
                    new TimelineTrack
                    {
                        Name = "Preset timeline",
                        SourceNodeId = "stale-timeline-source",
                    },
                },
            },
        };
        ApplyPreset(legacyTimelinePreset, autoRun: false);
        var restoredTimelineTrack = _timeline?.Project.Tracks
            .FirstOrDefault(track => track.Name == "Preset timeline");
        Check("timeline preset reattaches orphaned source",
            restoredTimelineTrack?.SourceNodeId == "preset-timeline-source" &&
            _nodes.TryGetValue("preset-timeline-source", out var presetTimelineEntry) &&
            presetTimelineEntry.Source is { Kind: SourceKind.Timeline } presetSource &&
            presetSource.Id == "preset-timeline-source");

        RenameTimelineTrack(restoredTimelineTrack!.Id, "Preset renamed");
        Check("timeline rename syncs node",
            _timeline?.Project.Tracks.FirstOrDefault()?.Name == "Preset renamed" &&
            _nodes["preset-timeline-source"].Title == "Preset renamed");
        TimelineUndo();
        Check("timeline rename undo syncs node",
            _timeline?.Project.Tracks.FirstOrDefault()?.Name == "Preset timeline" &&
            _nodes["preset-timeline-source"].Title == "Preset timeline");

        var guardSource = RegisterSource(new SourceNode("Preset validation guard", SourceKind.Tone), 0, 0);
        int nodeCountBeforeInvalid = _nodes.Count;
        bool invalidApplied = ApplyPreset(null!, autoRun: false);
        Check("invalid preset does not clear graph",
            !invalidApplied && _nodes.Count == nodeCountBeforeInvalid &&
            _nodes.ContainsKey(guardSource.Id));

        using (var badNetwork = OutputNode.CreateForNetwork("invalid.invalid", 4040))
        {
            try
            {
                badNetwork.Start();
            }
            catch
            {
            }

            Check("network start failure does not mark running", !badNetwork.IsRunning);
        }

        using (var ttsCapture = new TtsStreamCapture())
        {
            var oneSecond = new float[48000 * 2];
            for (int i = 0; i < 30; i++)
            {
                ttsCapture.Enqueue(oneSecond);
            }

            using var blocked = new CancellationTokenSource(100);
            bool backpressure = false;
            try
            {
                ttsCapture.Enqueue(oneSecond, blocked.Token);
            }
            catch (OperationCanceledException)
            {
                backpressure = true;
            }

            Check("tts capture queue is bounded", backpressure);
        }

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

        var networkPreset = new Preset
        {
            Outputs =
            {
                new PresetOutput
                {
                    IsNetwork = true,
                    Title = "Remote target",
                    NetworkHost = "example.com",
                    NetworkPort = 4040,
                },
            },
        };
        ApplyPreset(networkPreset, autoRun: false);
        Check("imported network output skipped",
            !_nodes.Values.Any(entry => entry.Output?.IsNetworkOutput == true));

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

        string vst3FilterRoot = Path.Combine(
            Path.GetTempPath(), "ruse-vst3-filter-" + Guid.NewGuid().ToString("N") + ".vst3");
        string vst3Single = Path.Combine(
            Path.GetTempPath(), "ruse-vst3-single-" + Guid.NewGuid().ToString("N") + ".vst3");
        bool vst3FilterOk = false;
        try
        {
            Directory.CreateDirectory(vst3FilterRoot);
            bool invalidRejected = !IsRunnableVst3Bundle(vst3FilterRoot);

            string vst3Contents = Path.Combine(vst3FilterRoot, "Contents", "x86_64-win");
            Directory.CreateDirectory(vst3Contents);
            File.WriteAllBytes(Path.Combine(vst3Contents, "plugin.vst3"), new byte[] { 0 });
            bool bundleAccepted = IsRunnableVst3Bundle(vst3FilterRoot);

            File.WriteAllBytes(vst3Single, new byte[] { 0 });
            bool singleAccepted = IsRunnableVst3Bundle(vst3Single);
            vst3FilterOk = invalidRejected && bundleAccepted && singleAccepted;
        }
        finally
        {
            try
            {
                Directory.Delete(vst3FilterRoot, recursive: true);
                File.Delete(vst3Single);
            }
            catch
            {
            }
        }

        Check("vst3 bundle filtering", vst3FilterOk);

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

        var seriesSource = RegisterSource(
            new SourceNode("Barge-in series", SourceKind.SystemLoopback), 40, 1800);
        var seriesModifierNode = new ModifierNode("Barge-in transcript");
        var seriesTranscriber = new TranscriptionProcessor();
        seriesTranscriber.SetTapId(TranscriptionProcessor.NextTapId());
        seriesModifierNode.Add(seriesTranscriber);
        var seriesModifier = RegisterModifier(seriesModifierNode, 400, 1800);
        ConnectEntries(seriesSource, seriesModifier, publish: false);

        var ttsSource = RegisterSource(
            new SourceNode("Barge-in TTS", SourceKind.Tts), 800, 1800);
        Check("barge-in requires key", !IsBargeKeyLinked(ttsSource, seriesTranscriber.TapId));
        ttsSource.TtsSourceNodeId = seriesModifier.Id;
        Check("barge-in ignores own source", !IsBargeKeyLinked(ttsSource, seriesTranscriber.TapId));
        ttsSource.TtsSourceNodeId = null;
        ConnectEntries(seriesModifier, ttsSource, kind: "barge-key", publish: false);
        Check("barge-in key link", IsBargeKeyLinked(ttsSource, seriesTranscriber.TapId));
        TranscriptionProcessor.RemoveTap(seriesTranscriber.TapId);

        var ttsPreset = new Preset { Name = "TTS preset link test" };
        ttsPreset.Sources.Add(new PresetSource
        {
            Id = "tts-preset-voice",
            Title = "Preset voice",
            Kind = SourceKind.Tts.ToString(),
            TtsModel = "kokoro-streaming",
            TtsSourceModifierIndex = 0,
            X = 40,
            Y = 2200,
        });
        ttsPreset.Sources.Add(new PresetSource
        {
            Id = "tts-preset-input",
            Title = "Preset input",
            Kind = SourceKind.SystemLoopback.ToString(),
            X = 40,
            Y = 2400,
        });
        ttsPreset.Modifiers.Add(new PresetModifier
        {
            Title = "Preset translator",
            Enabled = true,
            X = 400,
            Y = 2200,
            Processors = { new PresetProcessor { TypeId = TranslatorProcessor.Id } },
        });
        ApplyPreset(ttsPreset, autoRun: false);
        var presetTts = _nodes.Values.FirstOrDefault(entry => entry.Id == "tts-preset-voice");
        var presetProducer = _nodes.Values.FirstOrDefault(entry => entry.Modifier != null);
        Check("tts preset source link", presetTts?.TtsSourceNodeId == presetProducer?.Id);

        var groupA = RegisterSource(new SourceNode("Group A", SourceKind.Tone), 40, 2600);
        var groupB = RegisterSource(new SourceNode("Group B", SourceKind.Tone), 40, 2800);
        GroupMixerNodes(groupA.Id, groupB.Id);
        Check("mixer drag group",
            groupA.GroupId is { Length: > 0 } &&
            groupA.GroupId == groupB.GroupId &&
            groupA.Source?.GroupKey?.StartsWith("source:g:", StringComparison.Ordinal) == true);
        UngroupMixerGroup(groupA.GroupId!);
        Check("mixer ungroup", groupA.GroupId == null && groupB.GroupId == null);

        var networkLink = AddNetworkLink(40, 3000);
        NodeDto networkDto = CreateNodeDto(networkLink);
        Check("combined network node",
            networkLink.Source is { Kind: SourceKind.Network } &&
            networkLink.Output is { IsNetworkOutput: true } &&
            networkLink.ChatPort == ChatService.DefaultPort);
        Check("combined network dto",
            networkDto.Kind == "network" &&
            networkDto.IsNetworkInput &&
            networkDto.IsNetworkOutput &&
            networkDto.NetworkPort == NetworkPcm.DefaultPort &&
            networkDto.ChatPort == ChatService.DefaultPort);
        ReceiveNetworkText(networkLink.Id, "incoming network text");
        Check("network text feeds TTS source",
            TextFromSource(networkLink.Id)?.Text == "incoming network text");
        Check("network text is listed for TTS",
            BuildTtsSources(null).Any(source => source.Id == networkLink.Id && source.Kind == "network"));
        Check("network text is listed for translator",
            BuildNetworkTextSources(null).Any(source => source.Id == networkLink.Id));
        SetNetworkOutputHost(networkLink.Id, "192.168.1.50");
        SetNetworkVoicePort(networkLink.Id, 7901);
        SetNetworkChatPort(networkLink.Id, 7902);
        Preset combinedPreset = Preset.FromJson(
            BuildPreset(new HashSet<string> { networkLink.Id }).ToJson());
        ClearGraph();
        Check("combined network preset restore",
            ApplyPreset(combinedPreset, autoRun: false, trustRecorderPaths: true) &&
            _nodes.Values.FirstOrDefault(entry => entry.Kind == "network") is
            {
                Source: { Kind: SourceKind.Network, NetworkPort: 7901 },
                Output: { IsNetworkOutput: true, NetworkPort: 7901, NetworkHost: "192.168.1.50" },
                ChatPort: 7902,
            });
        RemoveEntryInternal(networkLink.Id);

        var textNote = CreateTextNote(40, 3200, content: "Note line one\nNote line two");
        Check("text note dto",
            CreateNodeDto(textNote) is { IsTextNote: true, TextContent: "Note line one\nNote line two" } &&
            TextFromSource(textNote.Id)?.Text == "Note line one\nNote line two" &&
            BuildTtsSources(null).Any(source => source.Id == textNote.Id && source.Kind == "note") &&
            BuildNetworkTextSources(null).Any(source => source.Id == textNote.Id && source.Kind == "note"));
        Preset notePreset = Preset.FromJson(
            BuildPreset(new HashSet<string> { textNote.Id }).ToJson());
        ClearGraph();
        Check("text note preset restore",
            ApplyPreset(notePreset, autoRun: false) &&
            _nodes.Values.FirstOrDefault(entry => entry.Kind == "text") is
            {
                TextContent: "Note line one\nNote line two",
            });

        ClearGraph();
        ClearHistory();
        return $"web parity: {string.Join(", ", checks)} -> {(pass ? "PASS" : "FAIL")}";
    }

    private static bool TestStorageFolderMigration()
    {
        string? previousOverride = AppSettings.OverrideDirectory;
        string root = Path.Combine(Path.GetTempPath(), "ruse-storage-test-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(Path.GetTempPath(), "ruse-storage-target-" + Guid.NewGuid().ToString("N"));
        string targetConflict = Path.Combine(Path.GetTempPath(), "ruse-storage-conflict-" + Guid.NewGuid().ToString("N"));
        string targetPartial = Path.Combine(Path.GetTempPath(), "ruse-storage-partial-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), "test");
            AppSettings.OverrideDirectory = root;

            StorageFolderChangeResult copied = StorageFolderService.Change(
                StorageFolderService.Data, target, copyExisting: true);
            string pointerPath = AppSettings.StorageLocationsPath;
            if (!copied.Ok ||
                !File.Exists(Path.Combine(target, "settings.json")) ||
                !File.Exists(pointerPath))
            {
                return false;
            }

            string pointerBefore = File.ReadAllText(pointerPath);
            Directory.CreateDirectory(targetConflict);
            File.WriteAllText(Path.Combine(targetConflict, "settings.json"), "old");
            StorageFolderChangeResult conflict = StorageFolderService.Change(
                StorageFolderService.Data, targetConflict, copyExisting: true);
            string pointerAfter = File.ReadAllText(pointerPath);
            if (conflict.Ok ||
                conflict.Conflicts != 1 ||
                !string.Equals(pointerBefore, pointerAfter, StringComparison.Ordinal))
            {
                return false;
            }

            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "b.txt"), "b");
            using (new FileStream(Path.Combine(root, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                StorageFolderChangeResult partial = StorageFolderService.Change(
                    StorageFolderService.Data, targetPartial, copyExisting: true);
                if (partial.Ok ||
                    partial.Failed == 0 ||
                    Directory.EnumerateFiles(targetPartial, "*", SearchOption.AllDirectories).Any())
                {
                    return false;
                }
            }

            return !StorageFolderService.Change(
                StorageFolderService.Data, AppContext.BaseDirectory, copyExisting: false).Ok;
        }
        catch
        {
            return false;
        }
        finally
        {
            AppSettings.OverrideDirectory = previousOverride;
            try
            {
                Directory.Delete(root, recursive: true);
                Directory.Delete(target, recursive: true);
                Directory.Delete(targetConflict, recursive: true);
                Directory.Delete(targetPartial, recursive: true);
            }
            catch
            {
            }
        }
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
