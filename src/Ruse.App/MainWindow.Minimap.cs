/// <summary>
/// Minimap part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Mini-map ----------

    /// <summary>How the mini-map decides whether to show itself.</summary>
    public enum MinimapVisibility
    {
        Always = 0,
        Never = 1,
        WhenZoomed = 2,
    }

    private MinimapOverlay? _minimap;
    private Point _minimapDragOrigin;

    private MinimapVisibility MinimapMode => (MinimapVisibility)(_settings.MinimapMode < 0 ? 0 : (_settings.MinimapMode > 2 ? 2 : _settings.MinimapMode));

    /// <summary>True when the mini-map should be on screen right now.</summary>
    private bool MinimapVisible => MinimapMode switch
    {
        MinimapVisibility.Always => true,
        MinimapVisibility.Never => false,
        _ => _zoomTransform.ScaleX > 1.15,
    };

    /// <summary>
    /// Paints the mini-map: every node as a small rectangle, the cables as
    /// hairlines, and the current view as a highlighted rectangle.
    /// </summary>
    private void DrawMinimap(DrawingContext dc, double width, double height)
    {
        if (_nodes.Count == 0)
        {
            return;
        }

        // World bounds of everything on the canvas, plus the visible area.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var vm in _nodes)
        {
            double nodeHeight = vm.Height;
            if (_nodeVisuals.TryGetValue(vm, out var body) && body.ActualHeight > 0)
            {
                nodeHeight = body.ActualHeight;
            }

            minX = Math.Min(minX, vm.X);
            minY = Math.Min(minY, vm.Y);
            maxX = Math.Max(maxX, vm.X + vm.Width);
            maxY = Math.Max(maxY, vm.Y + nodeHeight);
        }

        double scale = Math.Max(_zoomTransform.ScaleX, 0.01);
        double viewW = GraphScroll.ViewportWidth / scale;
        double viewH = GraphScroll.ViewportHeight / scale;
        double scrollX = GraphScroll.HorizontalOffset / scale;
        double scrollY = GraphScroll.VerticalOffset / scale;

        minX = Math.Min(minX, scrollX);
        minY = Math.Min(minY, scrollY);
        maxX = Math.Max(maxX, scrollX + viewW);
        maxY = Math.Max(maxY, scrollY + viewH);

        const double pad = 10;
        double worldW = Math.Max(maxX - minX, 1);
        double worldH = Math.Max(maxY - minY, 1);
        double factor = Math.Min((width - (pad * 2)) / worldW, (height - (pad * 2)) / worldH);

        Point ToMap(double x, double y) => new(
            pad + ((x - minX) * factor),
            pad + ((y - minY) * factor));

        // Cables first, so the nodes sit on top.
        var cablePen = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x55)), 1);
        foreach (var conn in _connections)
        {
            Point a = ToMap(conn.Start.X, conn.Start.Y);
            Point b = ToMap(conn.End.X, conn.End.Y);
            dc.DrawLine(cablePen, a, b);
        }

        foreach (var (vm, obj) in _nodeBacking)
        {
            double nodeHeight = vm.Height;
            if (_nodeVisuals.TryGetValue(vm, out var body) && body.ActualHeight > 0)
            {
                nodeHeight = body.ActualHeight;
            }

            Point topLeft = ToMap(vm.X, vm.Y);
            var color = vm.Kind switch
            {
                NodeKind.Source => Color.FromRgb(0x4C, 0x8D, 0xFF),
                NodeKind.Output => Color.FromRgb(0x39, 0xD9, 0x8A),
                _ => Color.FromRgb(0xE0, 0xA6, 0x3A),
            };

            dc.DrawRectangle(new SolidColorBrush(color), null,
                new Rect(topLeft.X, topLeft.Y, Math.Max(vm.Width * factor, 2), Math.Max(nodeHeight * factor, 2)));
        }

        // The current view.
        Point viewTopLeft = ToMap(scrollX, scrollY);
        var viewRect = new Rect(viewTopLeft.X, viewTopLeft.Y, Math.Max(viewW * factor, 4), Math.Max(viewH * factor, 4));
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)), 1.5), viewRect);
    }

    /// <summary>
    /// A scaled-down view of the whole canvas. Clicking or dragging inside it
    /// moves the main view, the way a map works.
    /// </summary>
    private sealed class MinimapOverlay : FrameworkElement
    {
        private readonly MainWindow _owner;

        public MinimapOverlay(MainWindow owner)
        {
            _owner = owner;
            Cursor = System.Windows.Input.Cursors.Hand;
        }

        protected override void OnRender(DrawingContext dc)
        {
            _owner.DrawMinimap(dc, ActualWidth, ActualHeight);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _owner.OnMinimapPointerDown(e.GetPosition(this), true);
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsMouseCaptured)
            {
                _owner.OnMinimapPointerDown(e.GetPosition(this), false);
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
    }

    private void OnMinimapPointerDown(Point at, bool startDrag)
    {
        if (startDrag)
        {
            _minimapDragOrigin = at;
        }

        // Map the click back to world coordinates and centre the view there.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var vm in _nodes)
        {
            double nodeHeight = vm.Height;
            if (_nodeVisuals.TryGetValue(vm, out var body) && body.ActualHeight > 0)
            {
                nodeHeight = body.ActualHeight;
            }

            minX = Math.Min(minX, vm.X);
            minY = Math.Min(minY, vm.Y);
            maxX = Math.Max(maxX, vm.X + vm.Width);
            maxY = Math.Max(maxY, vm.Y + nodeHeight);
        }

        if (minX > maxX)
        {
            return;
        }

        double scale = Math.Max(_zoomTransform.ScaleX, 0.01);
        double viewW = GraphScroll.ViewportWidth / scale;
        double viewH = GraphScroll.ViewportHeight / scale;
        double scrollX = GraphScroll.HorizontalOffset / scale;
        double scrollY = GraphScroll.VerticalOffset / scale;

        minX = Math.Min(minX, scrollX);
        minY = Math.Min(minY, scrollY);
        maxX = Math.Max(maxX, scrollX + viewW);
        maxY = Math.Max(maxY, scrollY + viewH);

        const double pad = 10;
        double worldW = Math.Max(maxX - minX, 1);
        double worldH = Math.Max(maxY - minY, 1);
        double mapW = _minimap?.ActualWidth ?? 210;
        double mapH = _minimap?.ActualHeight ?? 150;
        double factor = Math.Min((mapW - (pad * 2)) / worldW, (mapH - (pad * 2)) / worldH);
        if (factor <= 0)
        {
            return;
        }

        double worldX = minX + ((at.X - pad) / factor);
        double worldY = minY + ((at.Y - pad) / factor);

        GraphScroll.ScrollToHorizontalOffset((worldX - (viewW / 2)) * scale);
        GraphScroll.ScrollToVerticalOffset((worldY - (viewH / 2)) * scale);
    }

    private void UpdateMinimapVisibility()
    {
        bool visible = MinimapVisible && _nodes.Count > 0;

        if (visible && _minimap == null)
        {
            _minimap = new MinimapOverlay(this);
            MinimapHost.Children.Add(_minimap);
        }

        if (_minimap != null)
        {
            MinimapHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _minimap.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                _minimap.InvalidateVisual();
            }
        }
    }
}
