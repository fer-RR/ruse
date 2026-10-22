/// <summary>
/// Transport part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Transport ----------

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        _graph.AutoRun = true;
        int started = 0;
        foreach (var (vm, obj) in _nodeBacking)
        {
            try
            {
                switch (obj)
                {
                    case SourceNode source when !source.IsRunning:
                        source.Start();
                        started++;
                        break;
                    case OutputNode output when !output.IsRunning:
                        output.Start();
                        break;
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Start failed for '{vm.Title}': {ex.Message}");
                AppLog.Error($"Could not start '{vm.Title}'.", ex);
                return;
            }
        }

        SetStatus($"Routing started ({started} source(s), {_graph.Outputs.Count} output(s)).");
        SetRunState(true);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        StopAllRouting();
        SetStatus("Routing stopped.");
        SetRunState(false);
    }

    private void OnMicMuteClick(object sender, RoutedEventArgs e)
    {
        // A microphone kill switch: every device-input source is muted at once,
        // so the room cannot be picked up while the graph stays running.
        bool mute = !_micsMuted;
        _micsMuted = mute;

        int affected = 0;
        foreach (var (vm, obj) in _nodeBacking)
        {
            if (obj is SourceNode source && source.Kind == SourceKind.Device)
            {
                source.Muted = mute;
                vm.Muted = mute;
                affected++;
            }
        }

        MicMuteButton.Content = mute ? "🎙 Mic muted" : "🎙 Mute mic";
        MicMuteButton.Foreground = mute
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF5C5C"))
            : Brushes.White;

        SetStatus(mute
            ? $"Microphones muted ({affected} device input(s)). Everything else keeps running."
            : "Microphones live again.");
    }

    private void OnMasterMuteClick(object sender, RoutedEventArgs e)
    {
        OutputNode.MasterMuted = !OutputNode.MasterMuted;
        MasterMuteButton.Content = OutputNode.MasterMuted ? "🔊 Unmute all" : "🔇 Mute all";
        SetStatus(OutputNode.MasterMuted ? "All outputs muted." : "All outputs unmuted.");
    }

    private void OnMasterVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        OutputNode.MasterGain = (float)e.NewValue;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        StopAllRouting();

        foreach (var obj in _nodeBacking.Values)
        {
            switch (obj)
            {
                case SourceNode source:
                    source.Dispose();
                    break;
                case OutputNode output:
                    output.Dispose();
                    break;
            }
        }

        _nodeBacking.Clear();
        _nodeVisuals.Clear();
        _nodeHosts.Clear();
        _sourcePorts.Clear();
        _outputPorts.Clear();
        _meterSmoothed.Clear();
        _nodes.Clear();
        _connections.Clear();
        NodeCanvas.Children.Clear();
        ConnectionCanvas.Children.Clear();
        _graph.Sources.Clear();
        _graph.Outputs.Clear();
        UpdateEmptyHint();
        GraphScroll.ScrollToHome();
        UpdateGraphExtent();
        SetRunState(false);
        SetStatus("Graph cleared.");
    }
}
