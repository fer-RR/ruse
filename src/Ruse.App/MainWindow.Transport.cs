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

    private void OnStartClick(object sender, RoutedEventArgs e) => StartRouting();

    private void StartRouting()
    {
        _graph.AutoRun = true;
        int started = 0;
        int recordings = 0;
        int unarmedRecorders = 0;
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
                        if (output.IsRecording)
                        {
                            recordings++;
                        }
                        else if (output.IsFileRecorder && !output.HasRecordingPath)
                        {
                            unarmedRecorders++;
                        }

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

        string recordingNote = recordings > 0
            ? $", recording {recordings} file(s)"
            : string.Empty;
        string unarmedNote = unarmedRecorders > 0
            ? $"; {unarmedRecorders} recorder(s) need a file"
            : string.Empty;
        SetStatus($"Routing started ({started} source(s), {_graph.Outputs.Count} output(s){recordingNote}){unarmedNote}.");
        SetRunState(true);
        UpdateRecorderVisuals();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        var stoppedRecorders = _nodeBacking.Values
            .OfType<OutputNode>()
            .Where(o => o.IsRecording)
            .ToList();

        StopAllRouting();
        int saved = stoppedRecorders.Count(o => string.IsNullOrEmpty(o.RecordingError));
        int failed = stoppedRecorders.Count - saved;

        string message = stoppedRecorders.Count == 0
            ? "Routing stopped."
            : failed > 0
                ? $"Routing stopped. {saved} recording(s) saved, {failed} failed."
                : $"Routing stopped. {saved} recording(s) saved.";
        SetStatus(message);
        SetRunState(false);
        UpdateRecorderVisuals();
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
        _recorderStatus.Clear();
        _recorderStatusBorders.Clear();
        _recorderPaths.Clear();
        _recorderFormats.Clear();
        _recorderChooseButtons.Clear();
        _recorderBadges.Clear();
        _nodeClipBadges.Clear();
        _nodeClipHoldUntil.Clear();
        _clipWasActive = false;
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
        UpdateGlobalRecordingIndicator(0, 0, null);
        SetStatus("Graph cleared.");
    }
}
