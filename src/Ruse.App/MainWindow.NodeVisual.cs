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

    private void BuildNodeVisual(NodeViewModel vm, bool isSource,
        Action<StackPanel>? addControls = null, Func<FrameworkElement>? addHeaderBadge = null)
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

        // Badges live in their own column so a long title trims instead of
        // running underneath REC/CLIP.
        var headerBadges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var headerContent = new Grid();
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(headerText, 0);
        Grid.SetColumn(headerBadges, 1);
        headerContent.Children.Add(headerText);
        headerContent.Children.Add(headerBadges);

        var header = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.Hand,
            ToolTip = "Double-click to rename",
            Child = headerContent,
        };

        if (addHeaderBadge?.Invoke() is { } extraBadge)
        {
            headerBadges.Children.Add(extraBadge);
        }

        // Outputs can overload without the global CLIP light saying which one.
        if (vm.Kind == NodeKind.Output)
        {
            var clipBadge = CreateNodeClipBadge();
            headerBadges.Children.Add(clipBadge);
            _nodeClipBadges[vm] = clipBadge;
        }
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

        addControls?.Invoke(stack);

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
            BringNodeToFront(vm);
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
                bool moved = Math.Abs(newX - dragStartX) >= 0.5 ||
                             Math.Abs(newY - dragStartY) >= 0.5;
                if (moved)
                {
                    Point free = FindNearestFreeNodePosition(vm, newX, newY);
                    newX = free.X;
                    newY = free.Y;
                }

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
        BringNodeToFront(vm);
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
    private readonly Dictionary<NodeViewModel, TextBlock> _recorderStatus = new();
    private readonly Dictionary<NodeViewModel, Border> _recorderStatusBorders = new();
    private readonly Dictionary<NodeViewModel, TextBlock> _recorderPaths = new();
    private readonly Dictionary<NodeViewModel, ComboBox> _recorderFormats = new();
    private readonly Dictionary<NodeViewModel, Button> _recorderChooseButtons = new();
    private readonly Dictionary<NodeViewModel, RecorderBadgeVisuals> _recorderBadges = new();
    private readonly Dictionary<NodeViewModel, Border> _nodeClipBadges = new();
    private readonly Dictionary<NodeViewModel, long> _nodeClipHoldUntil = new();

    private sealed class RecorderBadgeVisuals
    {
        public required Border Badge { get; init; }
        public required TextBlock Text { get; init; }
        public required Ellipse Dot { get; init; }
    }

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

    private static readonly SolidColorBrush RecorderIdleBrush = MakeBrush("#8A93A6");
    private static readonly SolidColorBrush RecorderLiveBrush = MakeBrush("#FF6B6B");
    private static readonly SolidColorBrush RecorderDoneBrush = MakeBrush("#39D98A");
    private static readonly SolidColorBrush RecorderErrorBrush = MakeBrush("#FF9A62");
    private static readonly SolidColorBrush RecorderTextLiveBrush = MakeBrush("#FFB0B0");
    private static readonly SolidColorBrush RecorderBadgeLiveBack = MakeBrush("#4A1821");
    private static readonly SolidColorBrush RecorderBadgeArmedBack = MakeBrush("#173A2A");
    private static readonly SolidColorBrush RecorderBadgeIdleBack = MakeBrush("#2A2F3D");
    private static readonly SolidColorBrush RecorderBadgeErrorBack = MakeBrush("#4A2A1A");
    private static readonly SolidColorBrush RecorderStatusIdleBack = MakeBrush("#171B23");
    private static readonly SolidColorBrush RecorderStatusArmedBack = MakeBrush("#14291F");
    private static readonly SolidColorBrush RecorderStatusLiveBack = MakeBrush("#35151D");
    private static readonly SolidColorBrush RecorderStatusErrorBack = MakeBrush("#332016");
    private static readonly SolidColorBrush ClipBadgeBack = MakeBrush("#5A1B25");

    private static SolidColorBrush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Border CreateNodeClipBadge() => new()
    {
        Background = ClipBadgeBack,
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(5, 1, 5, 2),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
        ToolTip = "Output is clipping",
        Child = new TextBlock
        {
            Text = "CLIP",
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
        },
    };

    /// <summary>
    /// Adds the file destination controls to a recorder node: format, path and
    /// the live save state. The rest of the node (volume, meter, ports) is the
    /// same as any other output.
    /// </summary>
    private void AddRecorderControls(StackPanel stack, NodeViewModel vm, OutputNode output)
    {
        var label = new TextBlock
        {
            Text = "SAVE TO FILE",
            Foreground = RecorderIdleBrush,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(12, 4, 12, 2),
        };

        var formatBox = new ComboBox
        {
            ItemsSource = AudioFileFormats.All,
            SelectedItem = AudioFileFormats.Get(output.RecordingFormat),
            Margin = new Thickness(12, 0, 12, 4),
            FontSize = 11,
            ToolTip = "Format for the next recording",
        };

        var pathText = new TextBlock
        {
            FontSize = 10,
            Foreground = RecorderIdleBrush,
            Margin = new Thickness(12, 0, 12, 4),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var chooseButton = new Button
        {
            Content = "Choose file...",
            Margin = new Thickness(12, 0, 12, 4),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 11,
        };

        var status = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = RecorderIdleBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var statusBorder = new Border
        {
            Background = RecorderStatusIdleBack,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(12, 0, 12, 4),
            Child = status,
        };

        formatBox.SelectionChanged += (_, _) =>
        {
            if (formatBox.SelectedItem is not AudioFileFormatInfo info ||
                info.Format == output.RecordingFormat)
            {
                return;
            }

            if (output.IsRecording)
            {
                formatBox.SelectedItem = AudioFileFormats.Get(output.RecordingFormat);
                return;
            }

            string path = output.RecordingPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                path = System.IO.Path.ChangeExtension(path, info.Extension);
            }

            output.SetRecordingDestination(path, info.Format);
            UpdateRecorderPathText(output, pathText);
            UpdateRecorderVisuals();
            SetStatus($"Recording format: {info.DisplayName}.");
        };

        chooseButton.Click += (_, _) => ChooseRecordingFile(output, formatBox, pathText);

        stack.Children.Add(label);
        stack.Children.Add(formatBox);
        stack.Children.Add(chooseButton);
        stack.Children.Add(pathText);
        stack.Children.Add(statusBorder);

        _recorderFormats[vm] = formatBox;
        _recorderChooseButtons[vm] = chooseButton;
        _recorderPaths[vm] = pathText;
        _recorderStatus[vm] = status;
        _recorderStatusBorders[vm] = statusBorder;

        UpdateRecorderPathText(output, pathText);
        UpdateRecorderVisuals();
    }

    private void ChooseRecordingFile(OutputNode output, ComboBox formatBox, TextBlock pathText)
    {
        if (output.IsRecording)
        {
            return;
        }

        var info = formatBox.SelectedItem as AudioFileFormatInfo ??
                   AudioFileFormats.Get(output.RecordingFormat);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Record the mix to a file",
            Filter = info.Filter,
            DefaultExt = info.Extension,
            AddExtension = true,
            OverwritePrompt = true,
        };

        string current = output.RecordingPath;
        if (!string.IsNullOrWhiteSpace(current))
        {
            dialog.InitialDirectory = System.IO.Path.GetDirectoryName(current) ??
                                      Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            dialog.FileName = System.IO.Path.GetFileName(current);
        }
        else
        {
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            dialog.FileName = $"Ruse recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.{info.Extension}";
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string path = dialog.FileName;
        if (!string.Equals(System.IO.Path.GetExtension(path).TrimStart('.'),
                info.Extension, StringComparison.OrdinalIgnoreCase))
        {
            // The selected format owns the extension; a typed .wav must not
            // receive MP3 bytes.
            path = System.IO.Path.ChangeExtension(path, info.Extension);
        }

        output.SetRecordingDestination(path, info.Format);
        UpdateRecorderPathText(output, pathText);
        UpdateRecorderVisuals();

        // If the graph is already running and this recorder is wired in, arm it
        // immediately instead of making the user press Stop and Start.
        if (_graph.AutoRun && output.Inputs.Count > 0)
        {
            _graph.EnsureRunning(output);
        }

        UpdateRecorderVisuals();
        SetStatus(output.IsRecording
            ? $"Recording to {path}."
            : $"Recording will be saved to {path}.");
    }

    private static void UpdateRecorderPathText(OutputNode output, TextBlock pathText)
    {
        string text = output.HasRecordingPath ? output.RecordingPath : "No file selected";
        if (pathText.Text != text)
        {
            pathText.Text = text;
        }

        pathText.ToolTip = output.HasRecordingPath ? output.RecordingPath : null;
        pathText.Foreground = output.HasRecordingPath ? Brushes.White : RecorderIdleBrush;
    }

    private FrameworkElement CreateRecorderHeaderBadge(NodeViewModel vm)
    {
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = RecorderIdleBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };

        var text = new TextBlock
        {
            Text = "NO FILE",
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badge = new Border
        {
            Background = RecorderBadgeIdleBack,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 6, 2),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            ToolTip = "Recorder status",
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { dot, text },
            },
        };

        _recorderBadges[vm] = new RecorderBadgeVisuals
        {
            Badge = badge,
            Text = text,
            Dot = dot,
        };

        return badge;
    }

    /// <summary>Refreshes recorder path, format, badge and live state from the timer tick.</summary>
    private void UpdateRecorderVisuals()
    {
        int recordingCount = 0;
        double longestRecording = 0;
        string? firstRecordingPath = null;

        foreach (var (vm, obj) in _nodeBacking)
        {
            if (obj is not OutputNode { IsFileRecorder: true } output)
            {
                continue;
            }

            if (_recorderPaths.TryGetValue(vm, out var pathText))
            {
                UpdateRecorderPathText(output, pathText);
            }

            if (_recorderFormats.TryGetValue(vm, out var formatBox))
            {
                formatBox.IsEnabled = !output.IsRecording;
                var expected = AudioFileFormats.Get(output.RecordingFormat);
                if (!ReferenceEquals(formatBox.SelectedItem, expected))
                {
                    formatBox.SelectedItem = expected;
                }
            }

            if (_recorderChooseButtons.TryGetValue(vm, out var chooseButton))
            {
                chooseButton.IsEnabled = !output.IsRecording;
                string caption = output.HasRecordingPath ? "Change file..." : "Choose file...";
                if (!Equals(chooseButton.Content, caption))
                {
                    chooseButton.Content = caption;
                }
            }

            string badgeText;
            string statusText;
            SolidColorBrush badgeBack;
            SolidColorBrush badgeFore;
            SolidColorBrush dotBrush;
            SolidColorBrush statusBack;
            bool blinking = false;

            if (!string.IsNullOrEmpty(output.RecordingError))
            {
                badgeText = "ERR";
                statusText = "Error: " + output.RecordingError;
                badgeBack = RecorderBadgeErrorBack;
                badgeFore = RecorderErrorBrush;
                dotBrush = RecorderErrorBrush;
                statusBack = RecorderStatusErrorBack;
            }
            else if (!output.HasRecordingPath)
            {
                badgeText = "NO FILE";
                statusText = "Choose a file to arm";
                badgeBack = RecorderBadgeIdleBack;
                badgeFore = RecorderIdleBrush;
                dotBrush = RecorderIdleBrush;
                statusBack = RecorderStatusIdleBack;
            }
            else if (output.IsRecording)
            {
                badgeText = "REC";
                statusText = "REC  " + FormatRecordingTime(output.RecordedSeconds);
                badgeBack = RecorderBadgeLiveBack;
                badgeFore = RecorderTextLiveBrush;
                dotBrush = RecorderLiveBrush;
                statusBack = RecorderStatusLiveBack;
                blinking = true;
                recordingCount++;
                longestRecording = Math.Max(longestRecording, output.RecordedSeconds);
                firstRecordingPath ??= output.RecordingPath;
            }
            else if (output.RecordedSeconds > 0)
            {
                badgeText = "SAVED";
                statusText = "Saved  " + FormatRecordingTime(output.RecordedSeconds);
                badgeBack = RecorderBadgeArmedBack;
                badgeFore = RecorderDoneBrush;
                dotBrush = RecorderDoneBrush;
                statusBack = RecorderStatusArmedBack;
            }
            else
            {
                badgeText = "ARMED";
                statusText = "Armed - press Start";
                badgeBack = RecorderBadgeArmedBack;
                badgeFore = RecorderDoneBrush;
                dotBrush = RecorderDoneBrush;
                statusBack = RecorderStatusArmedBack;
            }

            if (_recorderBadges.TryGetValue(vm, out var badge))
            {
                if (badge.Badge.Visibility != Visibility.Visible)
                {
                    badge.Badge.Visibility = Visibility.Visible;
                }

                if (badge.Text.Text != badgeText)
                {
                    badge.Text.Text = badgeText;
                }

                if (!ReferenceEquals(badge.Badge.Background, badgeBack))
                {
                    badge.Badge.Background = badgeBack;
                }

                if (!ReferenceEquals(badge.Text.Foreground, badgeFore))
                {
                    badge.Text.Foreground = badgeFore;
                }

                if (!ReferenceEquals(badge.Dot.Fill, dotBrush))
                {
                    badge.Dot.Fill = dotBrush;
                }

                double dotOpacity = blinking && (Environment.TickCount64 / 500) % 2 == 0 ? 1.0 : 0.25;
                if (!blinking)
                {
                    dotOpacity = 1.0;
                }

                if (Math.Abs(badge.Dot.Opacity - dotOpacity) > 0.01)
                {
                    badge.Dot.Opacity = dotOpacity;
                }

                badge.Badge.ToolTip = statusText;
            }

            if (_recorderStatus.TryGetValue(vm, out var status))
            {
                if (status.Text != statusText)
                {
                    status.Text = statusText;
                }

                if (!ReferenceEquals(status.Foreground, badgeFore))
                {
                    status.Foreground = badgeFore;
                }

                status.ToolTip = statusText;
            }

            if (_recorderStatusBorders.TryGetValue(vm, out var statusBorder) &&
                !ReferenceEquals(statusBorder.Background, statusBack))
            {
                statusBorder.Background = statusBack;
            }

            // Recording and error borders are owned by UpdateNodeAppearance so
            // drop highlights and the muted-node setting cannot fight them.
            UpdateNodeAppearance(vm);
        }

        UpdateGlobalRecordingIndicator(recordingCount, longestRecording, firstRecordingPath);
    }

    private void UpdateGlobalRecordingIndicator(int count, double seconds, string? path)
    {
        if (count <= 0)
        {
            if (GlobalRecordChip.Visibility != Visibility.Collapsed)
            {
                GlobalRecordChip.Visibility = Visibility.Collapsed;
            }

            return;
        }

        if (GlobalRecordChip.Visibility != Visibility.Visible)
        {
            GlobalRecordChip.Visibility = Visibility.Visible;
        }

        string text = $"REC {FormatRecordingTime(seconds)}" + (count > 1 ? $"  x{count}" : string.Empty);
        if (GlobalRecordText.Text != text)
        {
            GlobalRecordText.Text = text;
        }

        double opacity = (Environment.TickCount64 / 500) % 2 == 0 ? 1.0 : 0.25;
        if (Math.Abs(GlobalRecordDot.Opacity - opacity) > 0.01)
        {
            GlobalRecordDot.Opacity = opacity;
        }

        GlobalRecordChip.ToolTip = count == 1
            ? $"Recording to {path}"
            : $"{count} recordings active";
    }

    private static string FormatRecordingTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
    }

    private const double GridSize = 20;

    /// <summary>Breathing room left around a node after it is nudged clear.</summary>
    private const double NodeOverlapGap = 12;

    /// <summary>
    /// Left/right inset for a modifier's parameter rows, leaving room for the
    /// node's "IN"/"OUT" port labels so they never sit on top of the controls.
    /// </summary>
    private const double PortLabelGutter = 34;

    /// <summary>
    /// Raises a node above every other node. WPF draws same-Z siblings in
    /// child order, so moving one node forward means moving the rest back.
    /// This is called when a node is grabbed and when it is first created.
    /// </summary>
    private void BringNodeToFront(NodeViewModel vm)
    {
        if (!_nodeHosts.TryGetValue(vm, out var host))
        {
            return;
        }

        foreach (var other in _nodeHosts.Values)
        {
            if (!ReferenceEquals(other, host))
            {
                Panel.SetZIndex(other, 0);
            }
        }

        Panel.SetZIndex(host, 1);
    }

    /// <summary>
    /// Drops a node where it was released unless that would cover another node.
    /// The nearest clear slot is always beside an existing node's edge, so only
    /// those edge positions need to be tried; the first one at the shortest
    /// distance wins.
    /// </summary>
    private Point FindNearestFreeNodePosition(NodeViewModel vm, double x, double y)
    {
        var size = GetNodeSize(vm);
        var obstacles = new List<Rect>();
        foreach (var (other, otherBody) in _nodeVisuals)
        {
            if (ReferenceEquals(other, vm))
            {
                continue;
            }

            double width = otherBody.ActualWidth > 0 ? otherBody.ActualWidth : other.Width;
            double height = otherBody.ActualHeight > 0 ? otherBody.ActualHeight : other.Height;
            obstacles.Add(new Rect(other.X, other.Y, width, height));
        }

        var candidate = new Rect(x, y, size.Width, size.Height);
        if (!OverlapsAny(candidate, obstacles))
        {
            return new Point(x, y);
        }

        var xs = new List<double> { x };
        var ys = new List<double> { y };
        foreach (var obstacle in obstacles)
        {
            AddCandidate(xs, obstacle.Left - size.Width - NodeOverlapGap);
            AddCandidate(xs, obstacle.Right + NodeOverlapGap);
            AddCandidate(ys, obstacle.Top - size.Height - NodeOverlapGap);
            AddCandidate(ys, obstacle.Bottom + NodeOverlapGap);
        }

        var slots = new List<(double X, double Y, double Distance)>();
        foreach (double candidateX in xs)
        {
            foreach (double candidateY in ys)
            {
                double dx = candidateX - x;
                double dy = candidateY - y;
                slots.Add((candidateX, candidateY, (dx * dx) + (dy * dy)));
            }
        }

        slots.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        foreach (var slot in slots)
        {
            var rect = new Rect(slot.X, slot.Y, size.Width, size.Height);
            if (!OverlapsAny(rect, obstacles))
            {
                return new Point(slot.X, slot.Y);
            }
        }

        // A column past every obstacle is always free, so this is only a
        // safety net for degenerate sizes.
        double fallbackX = x;
        foreach (var obstacle in obstacles)
        {
            fallbackX = Math.Max(fallbackX, obstacle.Right + NodeOverlapGap);
        }

        fallbackX = SnapToGrid(fallbackX);
        while (OverlapsAny(new Rect(fallbackX, y, size.Width, size.Height), obstacles))
        {
            fallbackX += GridSize;
        }

        return new Point(fallbackX, y);
    }

    private (double Width, double Height) GetNodeSize(NodeViewModel vm)
    {
        if (_nodeVisuals.TryGetValue(vm, out var body) &&
            body.ActualWidth > 0 && body.ActualHeight > 0)
        {
            return (body.ActualWidth, body.ActualHeight);
        }

        return (vm.Width, vm.Height);
    }

    private static void AddCandidate(List<double> values, double value)
    {
        double snapped = Math.Max(0, SnapToGrid(value));
        if (!values.Contains(snapped))
        {
            values.Add(snapped);
        }
    }

    private static bool OverlapsAny(Rect candidate, List<Rect> obstacles)
    {
        foreach (var obstacle in obstacles)
        {
            if (candidate.IntersectsWith(obstacle))
            {
                return true;
            }
        }

        return false;
    }

    private static double SnapToGrid(double value) => Math.Round(value / GridSize) * GridSize;
}
