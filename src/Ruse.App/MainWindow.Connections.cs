/// <summary>
/// Connections part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Connecting ----------

    private void OnSourceConnectorDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Ellipse { Tag: NodeViewModel vm })
        {
            return;
        }

        _dragFrom = vm;
        _dragCurrent = e.GetPosition(ConnectionCanvas);

        Point dragStart = _sourcePorts.TryGetValue(vm, out var dragPort)
            ? dragPort.Anchor
            : new Point(vm.PortX, vm.PortY);

        _dragLine = new Path
        {
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4C8DFF")),
            StrokeThickness = CableThickness / Math.Max(_zoomTransform.ScaleX, 0.01),
            StrokeDashArray = new DoubleCollection { 4, 3 },
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = BuildCable(dragStart, _dragCurrent),
        };
        _dragFromAnchor = dragStart;
        ConnectionCanvas.Children.Add(_dragLine);
        NodeCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragLine == null)
        {
            return;
        }

        _dragCurrent = e.GetPosition(ConnectionCanvas);
        _dragLine.Data = BuildCable(_dragFromAnchor, _dragCurrent);

        // Show which node the cable would land on, so the user can aim at the
        // body of a tall node instead of hunting for its IN port.
        SetDropHighlight(FindDropTargetAt(_dragCurrent));
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragFrom == null || _dragLine == null)
        {
            return;
        }

        Point release = e.GetPosition(ConnectionCanvas);
        NodeCanvas.ReleaseMouseCapture();
        ConnectionCanvas.Children.Remove(_dragLine);
        _dragLine = null;

        var target = FindDropTargetAt(release);
        SetDropHighlight(null);

        if (target != null && !ReferenceEquals(target, _dragFrom) && TryConnect(_dragFrom, target))
        {
            // TryConnect already updated the status.
        }
        else
        {
            SetStatus(target == null
                ? "Connection cancelled — drop the cable on a node to connect."
                : $"Cannot connect '{_dragFrom.Title}' to '{target.Title}'.");
        }

        _dragFrom = null;
    }

    /// <summary>
    /// Creates the graph link between two canvas nodes and draws its cable.
    /// Handles every allowed pairing: source→output, source→modifier,
    /// modifier→output and modifier→modifier.
    /// </summary>
    private bool TryConnect(NodeViewModel from, NodeViewModel to)
    {
        if (!ConnectNodes(from, to))
        {
            return false;
        }

        PushConnect(from, to);
        return true;
    }

    /// <summary>
    /// Wires two nodes together. Returns false when the pairing is not allowed
    /// or the cable already exists. Does not touch the undo history.
    /// </summary>
    private bool ConnectNodes(NodeViewModel from, NodeViewModel to)
    {
        if (!_nodeBacking.TryGetValue(from, out var fromObj) ||
            !_nodeBacking.TryGetValue(to, out var toObj))
        {
            return false;
        }

        string description;
        switch (fromObj, toObj)
        {
            case (SourceNode source, OutputNode output):
                _graph.Connect(source, output);
                description = $"'{source.Title}' → '{output.Title}'";
                break;
            case (SourceNode source, ModifierNode modifier):
                _graph.ConnectSourceToModifier(source, modifier);
                description = $"'{source.Title}' → '{modifier.Title}'";
                break;
            case (ModifierNode modifier, OutputNode output):
                _graph.ConnectModifierToOutput(modifier, output);
                description = $"'{modifier.Title}' → '{output.Title}'";
                break;
            case (ModifierNode first, ModifierNode second):
                if (ReferenceEquals(first, second))
                {
                    return false;
                }

                _graph.ConnectModifierToModifier(first, second);
                description = $"'{first.Title}' → '{second.Title}'";
                break;
            default:
                return false;
        }

        // Drawing the same cable twice would stack two identical cables, so the
        // second attempt is reported instead of duplicated.
        if (_connections.Any(c => ReferenceEquals(c.From.Owner, from) && ReferenceEquals(c.To.Owner, to)))
        {
            SetStatus($"'{from.Title}' is already connected to '{to.Title}'.");
            return false;
        }

        AddConnectionVisual(from, to);
        SetStatus($"Connected {description}");
        _sounds.PlayClick();
        return true;
    }

    /// <summary>
    /// Finds the node under a canvas point that can accept a connection.
    ///
    /// The whole node body is a drop target, not just its IN port: nodes grow
    /// with their content (a modifier with many parameters is far taller than
    /// the nominal node height), so the real laid-out bounds are used here.
    /// When several nodes overlap, the smallest one wins — that is the one the
    /// user is most likely aiming at.
    /// </summary>
    private NodeViewModel? FindDropTargetAt(Point p)
    {
        NodeViewModel? best = null;
        double bestDistance = double.MaxValue;
        double bestArea = double.MaxValue;

        foreach (var (vm, _) in _outputPorts)
        {
            if (!_nodeVisuals.TryGetValue(vm, out var body))
            {
                continue;
            }

            if (!IsPointInside(body, p))
            {
                continue;
            }

            // When nodes overlap, the one the pointer is closest to the middle
            // of wins — that is the one the user is aiming at.
            Point origin = body.TranslatePoint(new Point(0, 0), ConnectionCanvas);
            double dx = p.X - (origin.X + (body.ActualWidth / 2));
            double dy = p.Y - (origin.Y + (body.ActualHeight / 2));
            double distance = (dx * dx) + (dy * dy);
            double area = body.ActualWidth * body.ActualHeight;

            if (distance < bestDistance - 0.5 || (Math.Abs(distance - bestDistance) <= 0.5 && area < bestArea))
            {
                bestDistance = distance;
                bestArea = area;
                best = vm;
            }
        }

        return best;
    }

    /// <summary>Hit-tests a point against a laid-out element in canvas space.</summary>
    private bool IsPointInside(FrameworkElement element, Point p)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        Point origin = element.TranslatePoint(new Point(0, 0), ConnectionCanvas);
        const double slack = 6;
        return p.X >= origin.X - slack && p.X <= origin.X + element.ActualWidth + slack &&
               p.Y >= origin.Y - slack && p.Y <= origin.Y + element.ActualHeight + slack;
    }

    private static readonly Brush NodeIdleBorder =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2F3D"));

    private static readonly Brush NodeMutedBorder =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF5C5C"));

    private static readonly Brush NodeRecordingBorder = MakeBrush("#FF6B6B");
    private static readonly Brush NodeRecordingErrorBorder = MakeBrush("#FF9A62");

    private static void SetNodeBorder(Border body, Brush brush, double thickness)
    {
        if (!ReferenceEquals(body.BorderBrush, brush))
        {
            body.BorderBrush = brush;
        }

        if (Math.Abs(body.BorderThickness.Left - thickness) > 0.01)
        {
            body.BorderThickness = new Thickness(thickness);
        }
    }

    /// <summary>
    /// Paints a node's border: the drop target wins, then a live recorder, a
    /// recorder error, a muted node, otherwise the normal idle border.
    /// </summary>
    private void UpdateNodeAppearance(NodeViewModel vm)
    {
        if (!_nodeVisuals.TryGetValue(vm, out var body))
        {
            return;
        }

        bool recording = false;
        bool recordingError = false;
        if (_nodeBacking.TryGetValue(vm, out var backing) &&
            backing is OutputNode { IsFileRecorder: true } recorder)
        {
            recording = recorder.IsRecording;
            recordingError = !string.IsNullOrEmpty(recorder.RecordingError);
        }

        if (ReferenceEquals(vm, _dropHighlight))
        {
            SetNodeBorder(body, new SolidColorBrush((Color)ColorConverter.ConvertFromString(vm.Accent)), 2);
        }
        else if (recording)
        {
            SetNodeBorder(body, NodeRecordingBorder, 2);
        }
        else if (recordingError)
        {
            SetNodeBorder(body, NodeRecordingErrorBorder, 2);
        }
        else if (_settings.HighlightMutedNodes && vm.Muted)
        {
            SetNodeBorder(body, NodeMutedBorder, 2);
        }
        else
        {
            SetNodeBorder(body, NodeIdleBorder, 1);
        }
    }

    /// <summary>Re-paints every node, e.g. after the muted-highlight setting changed.</summary>
    private void RefreshNodeAppearance()
    {
        foreach (var vm in _nodes)
        {
            UpdateNodeAppearance(vm);
        }
    }

    /// <summary>Highlights the node a dragged cable would connect to.</summary>
    private void SetDropHighlight(NodeViewModel? vm)
    {
        if (ReferenceEquals(_dropHighlight, vm))
        {
            return;
        }

        var previous = _dropHighlight;
        _dropHighlight = vm;

        if (previous != null)
        {
            UpdateNodeAppearance(previous);
        }

        if (vm != null)
        {
            UpdateNodeAppearance(vm);
        }
    }

    /// <summary>
    /// The colour of a cable leaving a node: the node's own accent, so a route
    /// can be read at a glance (blue = source, amber = effect, green = output).
    /// </summary>
    private static Brush CableBrushFor(NodeViewModel owner)
    {
        string accent = owner.Accent;
        if (string.IsNullOrWhiteSpace(accent))
        {
            accent = "#4C8DFF";
        }

        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent));
        }
        catch
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4C8DFF"));
        }
    }

    private void AddConnectionVisual(NodeViewModel source, NodeViewModel output)
    {
        if (!_sourcePorts.TryGetValue(source, out var fromPort) ||
            !_outputPorts.TryGetValue(output, out var toPort))
        {
            return;
        }

        if (_connections.Any(c => c.From == fromPort && c.To == toPort))
        {
            return;
        }

        var conn = new ConnectionViewModel
        {
            From = fromPort,
            To = toPort,
            Start = fromPort.Anchor,
            End = toPort.Anchor,
        };

        // The cable takes the colour of the node it leaves, so the signal path
        // can be read at a glance: blue leaves a source, amber leaves an
        // effect, green leaves an output.
        var cableColor = CableBrushFor(fromPort.Owner);

        var cable = new Path
        {
            Stroke = cableColor,
            StrokeThickness = CableThickness / Math.Max(_zoomTransform.ScaleX, 0.01),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0.9,
            IsHitTestVisible = false,
        };
        cable.Tag = conn;

        // Wide, invisible hit target so the thin cable is easy to click.
        var hitPath = new Path
        {
            Stroke = Brushes.Transparent,
            StrokeThickness = CableHitThickness / Math.Max(_zoomTransform.ScaleX, 0.01),
            Cursor = Cursors.Hand,
            ToolTip = "Click to disconnect (or right-click for options)",
        };
        hitPath.Tag = conn;
        hitPath.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                PromptRouteGain(conn);
            }
            else
            {
                RemoveConnection(conn);
                SetStatus("Connection removed.");
            }

            e.Handled = true;
        };

        var menu = new ContextMenu();
        var gainItem = new MenuItem { Header = "Set gain…" };
        gainItem.Click += (_, _) => PromptRouteGain(conn);
        menu.Items.Add(gainItem);
        menu.Items.Add(new Separator());
        var disconnectItem = new MenuItem { Header = "Disconnect" };
        disconnectItem.Click += (_, _) => RemoveConnection(conn);
        menu.Items.Add(disconnectItem);
        hitPath.ContextMenu = menu;

        conn.Cable = cable;
        conn.HitPath = hitPath;

        _connections.Add(conn);
        ConnectionCanvas.Children.Add(cable);
        ConnectionCanvas.Children.Add(hitPath);

        UpdateConnections();
    }

    private readonly System.Diagnostics.Stopwatch _startupClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Time from start-up to the first paint, in milliseconds.</summary>
    private double _startupMs;

    // Cost of the periodic UI work, in milliseconds (accumulated).
    private double _refreshMs;
    private double _filterMs;
    private double _meterMs;
    private int _refreshCount;
    private int _filterCount;
    private int _meterCount;
}
