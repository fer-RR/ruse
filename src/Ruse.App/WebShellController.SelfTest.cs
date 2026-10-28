using System.IO;
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

        var waveform = new WaveformProcessor();
        waveform.SetTapId(WaveformProcessor.NextTapId());
        var waveformSamples = new float[480];
        Array.Fill(waveformSamples, 0.25f);
        waveform.Process(waveformSamples, 240);
        var captured = WaveformProcessor.GetSamples(waveform.TapId);
        Check("waveform tap", captured != null &&
                              captured.Any(value => Math.Abs(value - 0.25f) < 0.01f) &&
                              waveformSamples.All(value => Math.Abs(value - 0.25f) < 0.001f));

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
            Check("standalone tap monitor", tapModifier.Outputs.Any(output => output.IsMonitor) &&
                                            _graph.Outputs.Any(output => output.IsMonitor) &&
                                            tapModifier.Outputs.Any(output => output.Inputs.Count > 0));

            var monitor = tapModifier.Outputs.First(output => output.IsMonitor);
            monitor.Start();
            tapSource.Source!.WriteTestSamples(tone);
            Thread.Sleep(160);
            int monitored = TranscriptionProcessor.ReadAvailable(tap.TapId, tapBytes);
            Check("standalone tap audio", monitored > 0);
            monitor.Stop();

            _graph.RemoveModifier(tapModifier);
            Check("tap monitor cleanup", _graph.Outputs.All(output => !output.IsMonitor));
            _graph.RemoveSource(tapSource.Source!);
            _nodes.Remove(tapSource.Id);
            TranscriptionProcessor.RemoveTap(tap.TapId);
        }
        finally
        {
            _graph.AutoRun = autoRun;
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

        var source = RegisterSource(
            new SourceNode("Web test source", SourceKind.Application, 12345), 40, 40);
        PushAddNode(source);
        Check("add", _nodes.Count == 1);
        Undo();
        Check("undo add", _nodes.Count == 0);
        Redo();
        Check("redo add", _nodes.Count == 1);

        var output = RegisterOutput(
            OutputNode.CreateForFile(string.Empty, AudioFileFormat.Wav24), 320, 40);
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

        ClearGraph();
        ClearHistory();
        return $"web parity: {string.Join(", ", checks)} -> {(pass ? "PASS" : "FAIL")}";
    }

    private void SaveChainFromTest(ChainSaveDto chain)
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(chain, _json);
        SaveChain(new BridgeMessage { Type = "chain-save", Payload = payload });
    }
}
