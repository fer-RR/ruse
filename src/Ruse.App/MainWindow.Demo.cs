/// <summary>
/// Demo part of the main window. The shared state lives in MainWindow.xaml.cs;
/// this file only holds the code for that area.
/// </summary>
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;
using Ruse.App.Ui;

namespace Ruse.App;

public partial class MainWindow
{
    // ---------- Demo graph (screenshots / first-run preview) ----------

    private readonly Dictionary<NodeViewModel, float> _demoLevels = new();
    private readonly Random _demoRng = new(7);

    /// <summary>
    /// Populates the canvas with a representative routing setup so the UI can be
    /// shown without depending on specific hardware. Only used with --demo.
    /// </summary>
    private void BuildDemoGraph()
    {
        var discord = AddSourceNode(
            new SourceNode("Discord", SourceKind.Application, 1234), 40, 40);
        var game = AddSourceNode(
            new SourceNode("Game (Counter-Strike)", SourceKind.Application, 5678), 40, 250);
        var mic = AddDeviceSourceNode(
            new DeviceItem(GetFirstDevice(DataFlow.Capture) ?? throw new InvalidOperationException()), 40, 460);
        var browser = AddSourceNode(
            new SourceNode("Spotify", SourceKind.Application, 91011), 40, 660);

        var headset = AddOutputNode(FirstOutputItem(), 640, 60);
        var speakers = AddOutputNode(SecondOutputItem() ?? FirstOutputItem(), 640, 430);

        var virtualVm = TryAddVirtual(1000, 60);

        var demoHighPass = AudioProcessorCatalog.Find(HighPassProcessor.Id) is { } highPassDescriptor
            ? AddModifierNode(highPassDescriptor, 340, 300)
            : null;
        var demoCompressor = AudioProcessorCatalog.Find("compressor") is { } compressorDescriptor
            ? AddModifierNode(compressorDescriptor, 340, 480)
            : null;
        var demoGain = AudioProcessorCatalog.Find(GainProcessor.Id) is { } gainDescriptor
            ? AddModifierNode(gainDescriptor, 340, 660)
            : null;

        var sources = new[] { discord, game, mic, browser };
        foreach (var s in sources)
        {
            if (s == null)
            {
                continue;
            }

            // Give every node a distinct level so the bars look alive.
            _demoLevels[s] = 0.3f + (float)_demoRng.NextDouble() * 0.5f;
        }

        ConnectDemo(discord, headset);
        ConnectDemo(game, headset);
        ConnectDemo(mic, headset);
        ConnectDemo(discord, speakers);
        ConnectDemo(browser, speakers);

        if (virtualVm != null)
        {
            // Route the mic through demo modifiers so the amber chain nodes are
            // visible on launch: mic → High-pass → Compressor → Gain → virtual mic.
            if (demoGain != null && demoHighPass != null && demoCompressor != null)
            {
                ConnectDemo(mic, demoHighPass);
                ConnectDemo(demoHighPass, demoCompressor);
                ConnectDemo(demoCompressor, demoGain);
                ConnectDemo(demoGain, virtualVm);
            }
            else if (demoGain != null && demoHighPass != null)
            {
                ConnectDemo(mic, demoGain);
                ConnectDemo(demoGain, demoHighPass);
                ConnectDemo(demoHighPass, virtualVm);
            }
            else if (demoGain != null)
            {
                ConnectDemo(mic, demoGain);
                ConnectDemo(demoGain, virtualVm);
            }
            else
            {
                ConnectDemo(mic, virtualVm);
            }

            ConnectDemo(game, virtualVm);
        }

        SetStatus("Demo graph — showing sample routing.");
        SetRunState(true);
    }

    /// <summary>
    /// Builds a graph that carries real audio from a running process through a
    /// modifier into the first output, so the UI can be checked end to end
    /// (meters, cable waveforms) with genuine signal. Used by --live-demo.
    /// </summary>
    public void BuildLiveGraph(uint processId)
    {
        var source = AddSourceNode(new SourceNode("Live audio", SourceKind.Application, processId), 40, 140);
        var modifier = AudioProcessorCatalog.Find("highpass") is { } descriptor
            ? AddModifierNode(descriptor, 400, 140)
            : null;
        var output = AddOutputNode(FirstOutputItem(), 740, 140);

        if (source != null && modifier != null)
        {
            ConnectDemo(source, modifier);
        }

        if (modifier != null && output != null)
        {
            ConnectDemo(modifier, output);
        }
        else if (source != null && output != null)
        {
            ConnectDemo(source, output);
        }

        SetStatus("Live graph — carrying real audio.");
        SetRunState(true);
    }

    private void ConnectDemo(NodeViewModel? source, NodeViewModel? output)
    {
        if (source == null || output == null)
        {
            return;
        }

        if (!_nodeBacking.TryGetValue(source, out var from) || !_nodeBacking.TryGetValue(output, out var to))
        {
            return;
        }

        switch (from, to)
        {
            case (SourceNode src, OutputNode dst):
                _graph.Connect(src, dst);
                break;
            case (SourceNode src, ModifierNode mod):
                _graph.ConnectSourceToModifier(src, mod);
                break;
            case (ModifierNode mod, OutputNode dst):
                _graph.ConnectModifierToOutput(mod, dst);
                break;
            case (ModifierNode a, ModifierNode b):
                _graph.ConnectModifierToModifier(a, b);
                break;
            default:
                return;
        }

        AddConnectionVisual(source, output);
    }
}
