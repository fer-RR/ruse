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
