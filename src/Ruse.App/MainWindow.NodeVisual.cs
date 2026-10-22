/// <summary>
/// NodeVisual part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Node visuals ----------

    private void BuildNodeVisual(NodeViewModel vm, bool isSource)
    {
        var accent = (Color)ColorConverter.ConvertFromString(vm.Accent);

        var headerText = new TextBlock
        {
            Text = vm.Title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        headerText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(NodeViewModel.Title))
        {
            Source = vm,
            Mode = System.Windows.Data.BindingMode.OneWay,
        });

        var header = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.Hand,
            ToolTip = "Double-click to rename",
            Child = headerText,
        };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                RenameNode(vm);
                e.Handled = true;
            }
        };

        var subtitle = new TextBlock
        {
            Text = vm.Subtitle,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A93A6")),
            FontSize = 11,
            Margin = new Thickness(12, 6, 12, 4),
        };

        TextBlock? micHint = null;
        if (!string.IsNullOrEmpty(vm.MicHint))
        {
            micHint = new TextBlock
            {
                Text = $"Pick \"{vm.MicHint}\" as mic",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#39D98A")),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 0, 12, 6),
            };
        }

        // The bar is always full width and scaled horizontally by the meter level.
        // A ScaleTransform only touches the render pass, so the 20 Hz meter
        // update never triggers a layout cycle.
        var meterScale = new ScaleTransform(0, 1);
        var meterBar = new Rectangle
        {
            Height = 6,
            RadiusX = 3,
            RadiusY = 3,
            Fill = MeterLowBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = meterScale,
            RenderTransformOrigin = new Point(0, 0.5),
        };
        var meterTrack = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#12141A")),
            CornerRadius = new CornerRadius(3),
            Height = 6,
            Margin = new Thickness(12, 0, 12, 10),
            Child = meterBar,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        meterBar.Tag = meterTrack;

        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 1.5,
            Value = 1.0,
            Margin = new Thickness(12, 2, 12, 4),
            Height = 20,
        };
        float lastVolume = (float)volume.Value;
        volume.ValueChanged += (_, _) =>
        {
            float now = (float)volume.Value;
            if (Math.Abs(now - lastVolume) > 0.0001f)
            {
                PushVolume(vm, lastVolume, now);
            }

            lastVolume = now;
        };

        var muteButton = new Button
        {
            Content = "Mute",
            Margin = new Thickness(0, 0, 0, 0),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 11,
            Width = 74,
        };
        muteButton.Click += (_, _) =>
        {
            bool wasMuted = vm.Muted;
            vm.Muted = !vm.Muted;
            muteButton.Content = vm.Muted ? "Unmute" : "Mute";
            PushMute(vm, wasMuted, vm.Muted);
        };

        var disconnectButton = new Button
        {
            Content = "Disconnect",
            Margin = new Thickness(0, 0, 0, 0),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 11,
            Width = 92,
        };
        disconnectButton.Click += (_, _) => DisconnectNode(vm);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 0, 12, 10),
        };
        buttonRow.Children.Add(muteButton);
        buttonRow.Children.Add(disconnectButton);

        // Right-click anywhere on the node for a disconnect menu.
        var nodeMenu = new ContextMenu();
        var menuDisconnect = new MenuItem { Header = "Disconnect all routes" };
        menuDisconnect.Click += (_, _) => DisconnectNode(vm);
        var menuRemove = new MenuItem { Header = "Remove node" };
        menuRemove.Click += (_, _) => RemoveNode(vm);
        nodeMenu.Items.Add(menuDisconnect);
        nodeMenu.Items.Add(menuRemove);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(subtitle);
        if (micHint != null)
        {
            stack.Children.Add(micHint);
        }

        stack.Children.Add(meterTrack);
        stack.Children.Add(volume);
        stack.Children.Add(buttonRow);

        var body = new Border
        {
            Width = vm.Width,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B1E27")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2F3D")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack,
            Cursor = Cursors.SizeAll,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.45,
                Color = Colors.Black,
            },
            ContextMenu = nodeMenu,
        };

        // Connector dot: sources connect from the right, outputs accept on the left.
        var connector = new Ellipse
        {
            Width = 16,
            Height = 16,
            Fill = new SolidColorBrush(accent),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            Cursor = Cursors.Cross,
        };
        connector.Tag = vm;
        if (isSource)
        {
            connector.HorizontalAlignment = HorizontalAlignment.Right;
            connector.Margin = new Thickness(0, 0, -8, 0);
            connector.MouseLeftButtonDown += OnSourceConnectorDown;
        }
        else
        {
            connector.HorizontalAlignment = HorizontalAlignment.Left;
            connector.Margin = new Thickness(-8, 0, 0, 0);
        }

        // Small port label next to the connector so it is clear which end is which.
        var portLabel = new TextBlock
        {
            Text = isSource ? "OUT" : "IN",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A93A6")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isSource ? new Thickness(0, 0, 18, 0) : new Thickness(18, 0, 0, 0),
            HorizontalAlignment = isSource ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };

        var host = new Grid();
        host.Children.Add(body);
        host.Children.Add(portLabel);
        host.Children.Add(connector);

        Canvas.SetLeft(host, vm.X);
        Canvas.SetTop(host, vm.Y);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NodeViewModel.X))
            {
                Canvas.SetLeft(host, vm.X);
                UpdateConnections();
            }
            else if (args.PropertyName == nameof(NodeViewModel.Y))
            {
                Canvas.SetTop(host, vm.Y);
                UpdateConnections();
            }
        };

        // Dragging the node body.
        bool dragging = false;
        double dragStartX = 0;
        double dragStartY = 0;
        Point dragOffset = default;
        body.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
                dragStartX = vm.X;
                dragStartY = vm.Y;
            _selected = vm;
            dragOffset = e.GetPosition(NodeCanvas);
            dragOffset.X -= vm.X;
            dragOffset.Y -= vm.Y;
            body.CaptureMouse();
            e.Handled = false;
        };
        body.MouseMove += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            Point p = e.GetPosition(NodeCanvas);
            vm.X = p.X - dragOffset.X;
            vm.Y = p.Y - dragOffset.Y;
        };
        body.MouseLeftButtonUp += (_, _) =>
        {
            if (dragging)
            {
                double newX = SnapToGrid(vm.X);
                double newY = SnapToGrid(vm.Y);
                vm.X = newX;
                vm.Y = newY;
                PushMoveNode(vm, dragStartX, dragStartY, newX, newY);
            }

            dragging = false;
            body.ReleaseMouseCapture();
        };

        _nodeVisuals[vm] = body;
        _nodeHosts[vm] = host;
        _nodeSliders[vm] = volume;
        _nodeMuteButtons[vm] = muteButton;
        body.Tag = meterBar;
        NodeCanvas.Children.Add(host);
        UpdateEmptyHint();
        UpdateGraphExtent();

        // Keep the slider/button in sync when the VM is changed from elsewhere
        // (preset load, demo graph, etc).
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NodeViewModel.Volume))
            {
                double v = Math.Clamp(vm.Volume, volume.Minimum, volume.Maximum);
                if (Math.Abs(volume.Value - v) > 0.001)
                {
                    volume.Value = v;
                }
            }
            else if (args.PropertyName == nameof(NodeViewModel.Muted))
            {
                muteButton.Content = vm.Muted ? "Unmute" : "Mute";
                UpdateNodeAppearance(vm);
            }
        };

        // Anchor for cable geometry.
        var port = new NodePortViewModel
        {
            Name = isSource ? "out" : "in",
            IsOutput = isSource,
            Owner = vm,
            Element = connector,
            Anchor = new Point(vm.PortX, vm.PortY),
        };
        if (isSource)
        {
            _sourcePorts[vm] = port;
        }
        else
        {
            _outputPorts[vm] = port;
        }

        AttachPortAnchor(port, host);
    }

    private readonly Dictionary<NodeViewModel, NodePortViewModel> _sourcePorts = new();
    private readonly Dictionary<NodeViewModel, NodePortViewModel> _outputPorts = new();

    /// <summary>
    /// Pins a port's anchor to the centre of its connector dot. The dot's
    /// position is decided by the node's content during layout, so the anchor is
    /// recomputed after every layout pass — that is what keeps the cables landing
    /// exactly on the circles rather than on the node's nominal corner.
    /// </summary>
    private void AttachPortAnchor(NodePortViewModel port, FrameworkElement host)
    {
        RefreshPortAnchor(port);
        host.LayoutUpdated += (_, _) =>
        {
            if (RefreshPortAnchor(port))
            {
                UpdateConnections();
            }
        };
    }

    private bool RefreshPortAnchor(NodePortViewModel port)
    {
        if (port.Element is not { } element || element.ActualWidth <= 0 || !NodeCanvas.IsAncestorOf(element))
        {
            return false;
        }

        Point center;
        try
        {
            center = element.TransformToAncestor(NodeCanvas)
                .Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (Math.Abs(center.X - port.Anchor.X) < 0.01 && Math.Abs(center.Y - port.Anchor.Y) < 0.01)
        {
            return false;
        }

        port.Anchor = center;
        return true;
    }

    private const double GridSize = 20;

    /// <summary>
    /// Left/right inset for a modifier's parameter rows, leaving room for the
    /// node's "IN"/"OUT" port labels so they never sit on top of the controls.
    /// </summary>
    private const double PortLabelGutter = 34;

    private static double SnapToGrid(double value) => Math.Round(value / GridSize) * GridSize;
}
