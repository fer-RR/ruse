/// <summary>
/// View part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Viewport (pan / scroll) ----------

    /// <summary>Extra working room kept to the right and below the furthest node.</summary>
    private const double GraphPadding = 80;

    /// <summary>
    /// Sizes the graph surface so it always fills the viewport but also grows to
    /// contain every node, plus a generous margin so the user can pan around
    /// freely. The world is measured in unscaled units; the host is sized in
    /// scaled units so the scrollbars line up with what is on screen.
    /// </summary>
    private void UpdateGraphExtent()
    {
        double viewportWidth = GraphScroll.ViewportWidth;
        double viewportHeight = GraphScroll.ViewportHeight;
        if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = GraphScroll.ActualWidth;
        }

        if (double.IsNaN(viewportHeight) || viewportHeight <= 0)
        {
            viewportHeight = GraphScroll.ActualHeight;
        }

        double scale = _zoomTransform.ScaleX;

        double contentWidth = 0;
        double contentHeight = 0;
        foreach (var vm in _nodes)
        {
            double nodeHeight = vm.Height;
            if (_nodeVisuals.TryGetValue(vm, out var body) && body.ActualHeight > 0)
            {
                nodeHeight = body.ActualHeight;
            }

            contentWidth = Math.Max(contentWidth, vm.X + vm.Width);
            contentHeight = Math.Max(contentHeight, vm.Y + nodeHeight);
        }

        // The world is always at least as large as the viewport at the current
        // zoom, and always leaves room to pan beyond the nodes.
        double worldWidth = Math.Max(viewportWidth / scale, contentWidth + CanvasMargin);
        double worldHeight = Math.Max(viewportHeight / scale, contentHeight + CanvasMargin);

        SetSize(GraphLayer, worldWidth, worldHeight);
        SetSize(GraphHost, worldWidth * scale, worldHeight * scale);
    }

    private static void SetSize(FrameworkElement element, double width, double height)
    {
        if (double.IsNaN(element.Width) || Math.Abs(element.Width - width) > 0.5)
        {
            element.Width = width;
        }

        if (double.IsNaN(element.Height) || Math.Abs(element.Height - height) > 0.5)
        {
            element.Height = height;
        }
    }

    /// <summary>Extra room around the nodes so the canvas can be panned.</summary>
    private const double CanvasMargin = 600;

    /// <summary>Zoom limits for the canvas.</summary>
    private const double MinZoom = 0.25;
    private const double MaxZoom = 2.5;

    private readonly ScaleTransform _zoomTransform = new(1, 1);

    /// <summary>Current canvas zoom factor (1 = 100%).</summary>
    public double ZoomFactor => _zoomTransform.ScaleX;

    /// <summary>Mouse wheel zooms the canvas around the pointer.</summary>
    private void OnGraphMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
        {
            return;
        }

        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        ZoomAt(e.GetPosition(GraphScroll), factor);
        e.Handled = true;
    }

    /// <summary>
    /// Scales the world so the point under the pointer stays under the pointer.
    /// </summary>
    private void ZoomAt(Point pointer, double factor)
    {
        double oldScale = _zoomTransform.ScaleX;
        double newScale = Math.Clamp(oldScale * factor, MinZoom, MaxZoom);
        if (Math.Abs(newScale - oldScale) < 0.0001)
        {
            return;
        }

        // Where the pointer sits in world coordinates right now.
        double worldX = (GraphScroll.HorizontalOffset + pointer.X) / oldScale;
        double worldY = (GraphScroll.VerticalOffset + pointer.Y) / oldScale;

        _zoomTransform.ScaleX = newScale;
        _zoomTransform.ScaleY = newScale;

        UpdateGraphExtent();
        UpdateLayout();

        // A ScrollViewer ignores scroll requests made while it is laying out, so
        // the anchored offset is applied on the next dispatcher pass instead.
        _pendingScrollX = (worldX * newScale) - pointer.X;
        _pendingScrollY = (worldY * newScale) - pointer.Y;
        _scrollPending = true;
        Dispatcher.BeginInvoke(new Action(ApplyPendingScroll), DispatcherPriority.Loaded);

        UpdateCableThickness();
        UpdateZoomIndicator();
    }

    private double _pendingScrollX;
    private double _pendingScrollY;
    private bool _scrollPending;

    private void ApplyPendingScroll()
    {
        if (!_scrollPending)
        {
            return;
        }

        _scrollPending = false;
        GraphScroll.ScrollToHorizontalOffset(_pendingScrollX);
        GraphScroll.ScrollToVerticalOffset(_pendingScrollY);
    }

    /// <summary>Resets the canvas zoom to 100%.</summary>
    private void ResetZoom()
    {
        double oldScale = _zoomTransform.ScaleX;
        if (Math.Abs(oldScale - 1) < 0.0001)
        {
            return;
        }

        _zoomTransform.ScaleX = 1;
        _zoomTransform.ScaleY = 1;
        UpdateGraphExtent();
        UpdateLayout();
        UpdateCableThickness();
        UpdateZoomIndicator();
    }

    /// <summary>
    /// Keeps the cables the same thickness on screen whatever the zoom is,
    /// because the world is scaled at render time.
    /// </summary>
    private void UpdateCableThickness()
    {
        double scale = Math.Max(_zoomTransform.ScaleX, 0.01);
        double thickness = CableThickness / scale;
        double hit = CableHitThickness / scale;

        foreach (var conn in _connections)
        {
            if (conn.Cable != null)
            {
                conn.Cable.StrokeThickness = thickness;
            }

            if (conn.HitPath != null)
            {
                conn.HitPath.StrokeThickness = hit;
            }
        }

        if (_dragLine != null)
        {
            _dragLine.StrokeThickness = thickness;
        }
    }

    private const double CableThickness = 2.5;
    private const double CableHitThickness = 16;

    private void OnGraphResized(object sender, SizeChangedEventArgs e) => UpdateGraphExtent();

    private bool _refreshOnActivate;

    /// <summary>Shows the current zoom level and lets the user reset it.</summary>
    private void UpdateZoomIndicator()
    {
        if (ZoomButton == null)
        {
            return;
        }

        ZoomButton.Content = $"{_zoomTransform.ScaleX * 100:0}%";
    }

    private void OnZoomButtonClick(object sender, RoutedEventArgs e) => ResetZoom();

    private bool _panning;
    private Point _panOrigin;
    private double _panOffsetX;
    private double _panOffsetY;

    /// <summary>Middle mouse button pans the viewport (touch panning is enabled in XAML).</summary>
    private void OnGraphPanDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _panning = true;
        _panOrigin = e.GetPosition(GraphScroll);
        _panOffsetX = GraphScroll.HorizontalOffset;
        _panOffsetY = GraphScroll.VerticalOffset;
        GraphScroll.CaptureMouse();
        GraphScroll.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    private void OnGraphPanMove(object sender, MouseEventArgs e)
    {
        if (!_panning || e.MiddleButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point p = e.GetPosition(GraphScroll);
        GraphScroll.ScrollToHorizontalOffset(_panOffsetX - (p.X - _panOrigin.X));
        GraphScroll.ScrollToVerticalOffset(_panOffsetY - (p.Y - _panOrigin.Y));
        e.Handled = true;
    }

    private void OnGraphPanUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        _panning = false;
        GraphScroll.ReleaseMouseCapture();
        GraphScroll.Cursor = null;
        e.Handled = true;
    }

    private bool? _runStateShown;

    private void SetRunState(bool running)
    {
        if (_runStateShown == running)
        {
            return;
        }

        _runStateShown = running;
        RunDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#39D98A" : "#4A5266"));
        RunLabel.Text = running ? "Routing" : "Idle";
        RunLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#39D98A" : "#8892A6"));

        // The start button doubles as the transport state: muted while idle and
        // accented while audio is actually flowing. It stays clickable so it can
        // always be used to start (or restart) the graph.
        StartButton.Style = (Style)FindResource(running ? "PrimaryButton" : "StartIdleButton");
    }

    /// <summary>Briefly pulses a node's border to draw attention to it.</summary>
    private void FlashNode(NodeViewModel vm)
    {
        if (!_nodeVisuals.TryGetValue(vm, out var body))
        {
            return;
        }

        var flashBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4C8DFF"));
        body.BorderBrush = flashBrush;
        body.BorderThickness = new Thickness(2);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UpdateNodeAppearance(vm);
        };
        timer.Start();
    }

    private void UpdateConnections()
    {
        foreach (var conn in _connections)
        {
            conn.Start = conn.From.Anchor;
            conn.End = conn.To.Anchor;
            var geometry = BuildCable(conn.Start, conn.End);

            if (conn.Cable != null)
            {
                conn.Cable.Data = geometry;
            }

            if (conn.HitPath != null)
            {
                conn.HitPath.Data = geometry;
            }
        }
    }

    /// <summary>
    /// Builds a slack-cable path between two connector dots: it leaves the
    /// source and enters the target horizontally, with a gentle downward sag
    /// (bigger over longer horizontal runs) instead of a stiff S-curve.
    /// </summary>
    private static Geometry BuildCable(Point start, Point end)
    {
        var (c1, c2) = CableControls(start, end);

        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new BezierSegment(c1, c2, end, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>The two control points that shape a slack cable.</summary>
    private static (Point C1, Point C2) CableControls(Point start, Point end)
    {
        double dx = Math.Abs(end.X - start.X);

        // Horizontal pull keeps the cable tangent to the dots' sides.
        double pull = Math.Max(24, dx * 0.35);

        // Sag grows with horizontal span and eases back when the nodes are
        // stacked vertically, so short/vertical links stay near-straight.
        double sag = Math.Clamp(dx * 0.16, 5, 48);

        return (new Point(start.X + pull, start.Y + sag), new Point(end.X - pull, end.Y + sag));
    }

    /// <summary>Point on a cubic bezier at parameter u (0..1).</summary>
    private static Point CubicPoint(Point p0, Point p1, Point p2, Point p3, double u)
    {
        double v = 1 - u;
        double a = v * v * v;
        double b = 3 * v * v * u;
        double c = 3 * v * u * u;
        double d = u * u * u;
        return new Point(
            (a * p0.X) + (b * p1.X) + (c * p2.X) + (d * p3.X),
            (a * p0.Y) + (b * p1.Y) + (c * p2.Y) + (d * p3.Y));
    }

    /// <summary>Tangent of a cubic bezier at parameter u (0..1).</summary>
    private static (double X, double Y) CubicTangent(Point p0, Point p1, Point p2, Point p3, double u)
    {
        double v = 1 - u;
        double a = 3 * v * v;
        double b = 6 * v * u;
        double c = 3 * u * u;
        return (
            (a * (p1.X - p0.X)) + (b * (p2.X - p1.X)) + (c * (p3.X - p2.X)),
            (a * (p1.Y - p0.Y)) + (b * (p2.Y - p1.Y)) + (c * (p3.Y - p2.Y)));
    }
}
