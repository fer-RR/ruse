using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Ui;

namespace Ruse.App;

public partial class MainWindow : Window
{
    private readonly AudioGraph _graph = new();
    private readonly ObservableCollection<NodeViewModel> _nodes = new();
    private readonly ObservableCollection<ConnectionViewModel> _connections = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _refreshTimer;

    private readonly Dictionary<NodeViewModel, object> _nodeBacking = new();
    private readonly Dictionary<NodeViewModel, Border> _nodeVisuals = new();
    private readonly Dictionary<NodeViewModel, UIElement> _nodeHosts = new();
    private readonly Dictionary<NodeViewModel, Slider> _nodeSliders = new();
    private readonly Dictionary<NodeViewModel, Button> _nodeMuteButtons = new();

    private NodeViewModel? _dragFrom;
    private Point _dragCurrent;
    private Path? _dragLine;
    private Point _dragFromAnchor;
    private NodeViewModel? _selected;
    private NodeViewModel? _dropHighlight;
    private bool _micsMuted;

    private readonly UiSoundPlayer _sounds = new();

    private Point _listDragStart;
    private object? _listDragItem;
    private ListBox? _listDragSource;
    private const string NodeDragFormat = "RuseNode";

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();

        // Modifiers are fixed at build time from the processor catalog.
        ModifiersList.ItemsSource = AudioProcessorCatalog.All;

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => UpdateMeters();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _refreshTimer.Tick += (_, _) => RefreshApps();
        _refreshTimer.Start();

        GraphScroll.SizeChanged += OnGraphResized;

        // Drive the cable waveforms from the render tick.
        CompositionTarget.Rendering += OnRender;

        // The whole graph world scales through this one transform, so the
        // cables and the nodes always stay aligned at any zoom level.
        GraphLayer.LayoutTransform = _zoomTransform;
        UpdateZoomIndicator();

        Loaded += (_, _) =>
        {
            RestoreWindowBounds();
            RefreshAllDevices();
            RefreshApps();
            _meterTimer.Start();

            if (App.DemoMode)
            {
                BuildDemoGraph();
            }
            else if (!App.UiTestMode && !App.LiveDemoMode && _settings.AutoLoadSession && System.IO.File.Exists(AppSettings.SessionPath))
            {
                TryLoadSession();
            }

            UpdateGraphExtent();
        };

        Closing += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRender;
            SaveWindowState();
            _sounds.Dispose();
        };
    }

    private readonly AppSettings _settings;

    private void RestoreWindowBounds()
    {
        if (!double.IsNaN(_settings.Width) && _settings.Width > 200)
        {
            Width = _settings.Width;
        }

        if (!double.IsNaN(_settings.Height) && _settings.Height > 200)
        {
            Height = _settings.Height;
        }

        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top) && IsOnScreen(_settings.Left, _settings.Top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.Left;
            Top = _settings.Top;
        }

        if (_settings.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static bool IsOnScreen(double left, double top)
    {
        double maxWidth = SystemParameters.VirtualScreenWidth;
        double maxHeight = SystemParameters.VirtualScreenHeight;
        double minLeft = SystemParameters.VirtualScreenLeft;
        double minTop = SystemParameters.VirtualScreenTop;
        return left >= minLeft - 20 && top >= minTop - 20 &&
               left < minLeft + maxWidth - 100 && top < minTop + maxHeight - 100;
    }

    private void SaveWindowState()
    {
        try
        {
            _settings.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.Width = Width;
                _settings.Height = Height;
                _settings.Left = Left;
                _settings.Top = Top;
            }

            _settings.Save();

            // Persist the current graph as the next session (skip demo placeholder
            // and self-tests, which must never overwrite the user's real session).
            if (!App.DemoMode && !App.UiTestMode && _nodeBacking.Count > 0)
            {
                System.IO.Directory.CreateDirectory(AppSettings.Directory);
                System.IO.File.WriteAllText(AppSettings.SessionPath, BuildPreset().ToJson());
            }
        }
        catch
        {
        }
    }

    private void TryLoadSession()
    {
        try
        {
            var preset = Preset.FromJson(System.IO.File.ReadAllText(AppSettings.SessionPath));
            ApplyPreset(preset);
            SetStatus("Restored last session.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not restore last session: {ex.Message}");
        }
    }

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

    private static MMDevice? GetFirstDevice(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault();
    }

    private DeviceItem FirstOutputItem()
    {
        if (OutputsList.ItemsSource is IEnumerable<DeviceItem> items)
        {
            var first = items.FirstOrDefault();
            if (first != null)
            {
                return first;
            }
        }

        return new DeviceItem(GetFirstDevice(DataFlow.Render)
            ?? throw new InvalidOperationException("No render devices available."));
    }

    private DeviceItem? SecondOutputItem()
    {
        if (OutputsList.ItemsSource is IEnumerable<DeviceItem> items)
        {
            return items.Skip(1).FirstOrDefault();
        }

        return null;
    }

    private NodeViewModel? TryAddVirtual(double x, double y)
    {
        if (VirtualList.ItemsSource is IEnumerable<VirtualOutputInfo> virt && virt.Any())
        {
            return AddVirtualOutputNode(virt.First(), x, y);
        }

        return null;
    }

    private void RefreshAllDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _allOutputs = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new DeviceItem(d))
                .ToList();

            _allInputs = enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new DeviceItem(d))
                .ToList();

            var virtuals = VirtualDeviceCatalog.Discover();
            VirtualList.ItemsSource = virtuals;
            VirtualEmptyHint.Visibility = virtuals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            ApplyFilter();
            SetStatus("Devices refreshed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Device enumeration failed: {ex.Message}");
        }
    }

    private void RefreshApps()
    {
        try
        {
            var sessions = AudioSessionEnumerator.GetRenderSessions();

            // Preserve the user's selection/scroll across the periodic refresh
            // (bug: the list used to reset every 4s).
            var previous = AppsList.SelectedItem as AudioSessionInfo;
            _allApps = sessions;
            ApplyFilter();

            if (previous != null)
            {
                var match = _allApps.FirstOrDefault(a => a.ProcessId == previous.ProcessId);
                if (match != null)
                {
                    AppsList.SelectedItem = match;
                }
            }

            AppsCount.Text = sessions.Count.ToString();
        }
        catch (Exception ex)
        {
            SetStatus($"Session enumeration failed: {ex.Message}");
        }
    }

    private IReadOnlyList<AudioSessionInfo> _allApps = Array.Empty<AudioSessionInfo>();
    private List<DeviceItem> _allOutputs = new();
    private List<DeviceItem> _allInputs = new();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string query = SearchBox?.Text?.Trim() ?? string.Empty;

        AppsList.ItemsSource = Filter(_allApps, query, a => a.Label);
        OutputsList.ItemsSource = Filter(_allOutputs, query, d => d.FriendlyName);
        InputsList.ItemsSource = Filter(_allInputs, query, d => d.FriendlyName);
    }

    private static List<T> Filter<T>(IReadOnlyList<T> source, string query, Func<T, string> selector)
    {
        if (string.IsNullOrEmpty(query))
        {
            return source.ToList();
        }

        return source
            .Where(item => selector(item).Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private sealed class DeviceItem
    {
        public DeviceItem(MMDevice device)
        {
            Id = device.ID;
            FriendlyName = device.FriendlyName;
        }

        public string Id { get; }
        public string FriendlyName { get; }
    }

    // ---------- Sidebar interactions ----------

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshAllDevices();
        RefreshApps();
    }

    private void OnAppDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppsList.SelectedItem is AudioSessionInfo info)
        {
            AddSourceNode(new SourceNode(info.Label, SourceKind.Application, info.ProcessId));
        }
    }

    private void OnOutputDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OutputsList.SelectedItem is DeviceItem item)
        {
            AddOutputNode(item);
        }
    }

    private void OnInputDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (InputsList.SelectedItem is DeviceItem item)
        {
            AddDeviceSourceNode(item);
        }
    }

    private void OnVirtualDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VirtualList.SelectedItem is VirtualOutputInfo info)
        {
            AddVirtualOutputNode(info);
        }
    }

    private void OnModifierDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModifiersList.SelectedItem is AudioProcessorCatalog.Descriptor md)
        {
            AddModifierNode(md);
        }
    }

    // ---------- Drag from sidebar to canvas ----------

    private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _listDragSource = sender as ListBox;
        _listDragStart = e.GetPosition(this);
        _listDragItem = null;
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _listDragSource == null)
        {
            return;
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _listDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _listDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = (_listDragSource.SelectedItem);
        if (item == null)
        {
            return;
        }

        _listDragItem = item;

        object payload = _listDragSource switch
        {
            var lb when lb == OutputsList && item is DeviceItem od => new DropPayload(DropKind.OutputDevice, Device: od),
            var lb when lb == InputsList && item is DeviceItem id => new DropPayload(DropKind.CaptureDevice, Device: id),
            var lb when lb == AppsList && item is AudioSessionInfo ai => new DropPayload(DropKind.Application, App: ai),
            var lb when lb == VirtualList && item is VirtualOutputInfo vi => new DropPayload(DropKind.VirtualOutput, Virtual: vi),
            var lb when lb == ModifiersList && item is AudioProcessorCatalog.Descriptor md => new DropPayload(DropKind.Modifier, Modifier: md),
            _ => item,
        };

        var data = new DataObject(NodeDragFormat, payload);
        DragDrop.DoDragDrop(_listDragSource, data, DragDropEffects.Copy);
        _listDragItem = null;
    }

    private void OnListPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _listDragSource = null;
        _listDragStart = default;
    }

    private void OnCanvasDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(NodeDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(NodeDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(NodeDragFormat))
        {
            return;
        }

        var payload = e.Data.GetData(NodeDragFormat);
        Point dropPos = e.GetPosition(NodeCanvas);

        // Offset so the node lands roughly centred under the cursor.
        double x = Math.Max(0, dropPos.X - 95);
        double y = Math.Max(0, dropPos.Y - 40);

        switch (payload)
        {
            case DropPayload { Kind: DropKind.Application, App: { } info }:
                AddSourceNode(new SourceNode(info.Label, SourceKind.Application, info.ProcessId), x, y);
                break;
            case DropPayload { Kind: DropKind.VirtualOutput, Virtual: { } vInfo }:
                AddVirtualOutputNode(vInfo, x, y);
                break;
            case DropPayload { Kind: DropKind.OutputDevice, Device: { } od }:
                AddOutputNode(od, x, y);
                break;
            case DropPayload { Kind: DropKind.CaptureDevice, Device: { } id }:
                AddDeviceSourceNode(id, x, y);
                break;
            case DropPayload { Kind: DropKind.Modifier, Modifier: { } md }:
                AddModifierNode(md, x, y);
                break;
        }

        e.Handled = true;
    }

    private enum DropKind
    {
        Application,
        OutputDevice,
        CaptureDevice,
        VirtualOutput,
        Modifier,
    }

    private sealed record DropPayload(
        DropKind Kind,
        AudioSessionInfo? App = null,
        DeviceItem? Device = null,
        VirtualOutputInfo? Virtual = null,
        AudioProcessorCatalog.Descriptor? Modifier = null);

    private NodeViewModel? AddDeviceSourceNode(DeviceItem item, double? x = null, double? y = null)
    {
        var source = new SourceNode(item.FriendlyName, SourceKind.Device, deviceId: item.Id);
        return AddSourceNode(source, x, y);
    }

    private NodeViewModel? AddSourceNode(SourceNode source, double? x = null, double? y = null)
    {
        _graph.AddSource(source);

        var vm = new NodeViewModel
        {
            Id = source.Id,
            Title = source.Title,
            Subtitle = source.Kind switch
            {
                SourceKind.Application => "Application",
                SourceKind.Device => "Input device",
                _ => "System audio",
            },
            Accent = source.Kind == SourceKind.Application ? "#4C8DFF" : "#B06CFF",
            CanBeSource = true,
            X = x ?? (60 + (_nodes.Count * 30)),
            Y = y ?? (60 + (_nodes.Count * 30)),
        };

        vm.VolumeChanged = () =>
        {
            source.Gain = vm.Volume;
            source.Muted = vm.Muted;
        };

        _nodeBacking[vm] = source;
        _nodes.Add(vm);
        BuildNodeVisual(vm, isSource: true);
        SubscribeSourceFaults(source, vm);
        SetStatus($"Added source: {source.Title}");
        return vm;
    }

    private void SubscribeSourceFaults(SourceNode source, NodeViewModel vm)
    {
        source.Faulted += (node, reason) => Dispatcher.BeginInvoke(() =>
        {
            if (source.Kind == SourceKind.Application)
            {
                vm.Subtitle = "Application — stopped";
            }
            else
            {
                vm.Subtitle = "Disconnected";
            }

            SetStatus($"'{node.Title}' stopped: {reason}");
            StopRoutingIfNoOutputsRunning();
        });
    }

    private void SubscribeOutputFaults(OutputNode output, NodeViewModel vm)
    {
        output.Faulted += (node, reason) => Dispatcher.BeginInvoke(() =>
        {
            vm.Subtitle = "Disconnected";
            SetStatus($"Output '{node.Title}' stopped: {reason}");
            StopRoutingIfNoOutputsRunning();
        });
    }

    private void StopRoutingIfNoOutputsRunning()
    {
        if (!_graph.Outputs.Any(o => o.IsRunning))
        {
            StopAllRouting();
            SetRunState(false);
        }
    }

    private void StopAllRouting()
    {
        _graph.AutoRun = false;
        foreach (var obj in _nodeBacking.Values)
        {
            switch (obj)
            {
                case SourceNode source:
                    source.Stop();
                    break;
                case OutputNode output:
                    output.Stop();
                    break;
            }
        }
    }

    private NodeViewModel? FindOutputVmByDeviceId(string deviceId)
    {
        foreach (var (vm, obj) in _nodeBacking)
        {
            if (obj is OutputNode existing &&
                string.Equals(existing.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return vm;
            }
        }

        return null;
    }

    private NodeViewModel? AddOutputNode(DeviceItem item, double? x = null, double? y = null)
    {
        var duplicate = FindOutputVmByDeviceId(item.Id);
        if (duplicate != null)
        {
            FlashNode(duplicate);
            SetStatus($"'{duplicate.Title}' is already on the canvas.");
            return duplicate;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(item.Id);
            var output = _graph.AddOutput(device);

            var vm = new NodeViewModel
            {
                Id = output.Id,
                Title = output.Title,
                Subtitle = "Output device",
                Accent = "#39D98A",
                Kind = NodeKind.Output,
                CanBeSource = false,
                X = x ?? (620 + (_nodes.Count * 20)),
                Y = y ?? (80 + (_nodes.Count * 30)),
            };

            vm.VolumeChanged = () =>
            {
                output.Volume = vm.Volume;
                output.Muted = vm.Muted;
            };

            _nodeBacking[vm] = output;
            _nodes.Add(vm);
            BuildNodeVisual(vm, isSource: false);
            SubscribeOutputFaults(output, vm);
            SetStatus($"Added output: {output.Title}");
            return vm;
        }
        catch (Exception ex)
        {
            SetStatus($"Could not add output: {ex.Message}");
            return null;
        }
    }

    private void OnAddSystemClick(object sender, RoutedEventArgs e)
    {
        AddSourceNode(new SourceNode("System audio (everything)", SourceKind.SystemLoopback));
    }

    private NodeViewModel? AddVirtualOutputNode(VirtualOutputInfo info, double? x = null, double? y = null)
    {
        var duplicate = FindOutputVmByDeviceId(info.RenderDeviceId);
        if (duplicate != null)
        {
            FlashNode(duplicate);
            SetStatus($"'{duplicate.Title}' is already on the canvas.");
            return duplicate;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(info.RenderDeviceId);
            var output = _graph.AddOutput(device);

            var vm = new NodeViewModel
            {
                Id = output.Id,
                Title = info.RenderFriendlyName,
                Subtitle = $"{info.Provider} virtual output",
                Accent = "#E0A33A",
                Kind = NodeKind.Output,
                CanBeSource = false,
                MicHint = info.CaptureFriendlyName,                X = x ?? (640 + (_nodes.Count * 20)),
                Y = y ?? (120 + (_nodes.Count * 30)),
            };

            vm.VolumeChanged = () =>
            {
                output.Volume = vm.Volume;
                output.Muted = vm.Muted;
            };

            _nodeBacking[vm] = output;
            _nodes.Add(vm);
            BuildNodeVisual(vm, isSource: false);
            SubscribeOutputFaults(output, vm);
            SetStatus($"Added virtual output: {info.RenderFriendlyName}. In Discord/OBS select \"{info.CaptureFriendlyName}\" as the microphone.");
            return vm;
        }
        catch (Exception ex)
        {
            SetStatus($"Could not add virtual output: {ex.Message}");
            return null;
        }
    }

    private NodeViewModel? AddModifierNode(AudioProcessorCatalog.Descriptor descriptor, double? x = null, double? y = null)
    {
        var modifier = new ModifierNode(descriptor.DisplayName);
        modifier.Add(descriptor.Create());
        return AddModifierNodeCore(modifier, descriptor.DisplayName, x, y);
    }

    /// <summary>Registers an already-configured modifier node and builds its visual.</summary>
    private NodeViewModel? AddModifierNodeCore(ModifierNode modifier, string title, double? x = null, double? y = null)
    {
        _graph.AddModifier(modifier);

        var vm = new NodeViewModel
        {
            Id = modifier.Id,
            Title = title,
            Subtitle = "Modifier",
            Accent = "#E0A63A",
            Kind = NodeKind.Modifier,
            CanBeSource = true,
            X = x ?? (330 + (_nodes.Count * 20)),
            Y = y ?? (90 + (_nodes.Count * 30)),
        };

        _nodeBacking[vm] = modifier;
        _nodes.Add(vm);
        BuildModifierVisual(vm, modifier);
        SetStatus($"Added modifier: {title}. Connect a source into it, then it into an output.");
        return vm;
    }

    /// <summary>Builds the compact visual for a modifier: input and output ports plus its parameters.</summary>
    private void BuildModifierVisual(NodeViewModel vm, ModifierNode modifier)
    {
        var accent = (Color)ColorConverter.ConvertFromString(vm.Accent);

        var headerText = new TextBlock
        {
            Foreground = Brushes.Black,
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

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(subtitle);

        // A generic slider for every parameter the modifier's processors expose.
        foreach (var processor in modifier.Processors)
        {
            if (processor is not IParameterizedProcessor parameterized)
            {
                continue;
            }

            foreach (var parameter in parameterized.Parameters)
            {
                if (parameter.IsToggle)
                {
                    var check = new CheckBox
                    {
                        Content = parameter.Name,
                        IsChecked = parameter.Get() >= 0.5f,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C9D1E1")),
                        FontSize = 11,
                        Margin = new Thickness(PortLabelGutter, 2, PortLabelGutter, 6),
                    };
                    check.Checked += (_, _) => parameter.Set(1f);
                    check.Unchecked += (_, _) => parameter.Set(0f);
                    stack.Children.Add(check);
                    continue;
                }

                bool logarithmic = parameter.Logarithmic && parameter.Min > 0 && parameter.Max > parameter.Min;

                string Format(float value) =>
                    string.IsNullOrEmpty(parameter.Unit)
                        ? $"{parameter.Name}: {value:0.##}"
                        : $"{parameter.Name}: {value:0.#} {parameter.Unit}";

                var label = new TextBlock
                {
                    Text = Format(parameter.Get()),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A93A6")),
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(PortLabelGutter, 2, PortLabelGutter, 0),
                };

                var slider = new Slider
                {
                    Minimum = logarithmic ? Math.Log10(parameter.Min) : parameter.Min,
                    Maximum = logarithmic ? Math.Log10(parameter.Max) : parameter.Max,
                    Value = logarithmic
                        ? Math.Log10(Math.Clamp(parameter.Get(), parameter.Min, parameter.Max))
                        : Math.Clamp(parameter.Get(), parameter.Min, parameter.Max),
                    Margin = new Thickness(PortLabelGutter, 0, PortLabelGutter, 6),
                    Height = 20,
                };
                slider.ValueChanged += (_, _) =>
                {
                    float value = logarithmic ? (float)Math.Pow(10, slider.Value) : (float)slider.Value;
                    parameter.Set(value);
                    label.Text = Format(value);
                };

                stack.Children.Add(label);
                stack.Children.Add(slider);
            }
        }

        var removeButton = new Button
        {
            Content = "Remove",
            Margin = new Thickness(12, 0, 12, 10),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 11,
        };
        removeButton.Click += (_, _) => RemoveNode(vm);
        stack.Children.Add(removeButton);

        var nodeMenu = new ContextMenu();
        var menuDisconnect = new MenuItem { Header = "Disconnect all routes" };
        menuDisconnect.Click += (_, _) => DisconnectNode(vm);
        var menuRemove = new MenuItem { Header = "Remove node" };
        menuRemove.Click += (_, _) => RemoveNode(vm);
        nodeMenu.Items.Add(menuDisconnect);
        nodeMenu.Items.Add(menuRemove);

        var body = new Border
        {
            Width = vm.Width,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#221E16")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A3D22")),
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

        // Output connector (right) starts a drag; input connector (left) is the drop side.
        var outConnector = new Ellipse
        {
            Width = 16,
            Height = 16,
            Fill = new SolidColorBrush(accent),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            Cursor = Cursors.Cross,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -8, 0),
            ToolTip = "Drag to an output or another modifier",
        };
        outConnector.Tag = vm;
        outConnector.MouseLeftButtonDown += OnSourceConnectorDown;

        var inConnector = new Ellipse
        {
            Width = 16,
            Height = 16,
            Fill = new SolidColorBrush(accent),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-8, 0, 0, 0),
            ToolTip = "Drop a source or modifier here",
        };

        var outLabel = new TextBlock
        {
            Text = "OUT",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A93A6")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsHitTestVisible = false,
        };

        var inLabel = new TextBlock
        {
            Text = "IN",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A93A6")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };

        var host = new Grid();
        host.Children.Add(body);
        host.Children.Add(inLabel);
        host.Children.Add(outLabel);
        host.Children.Add(inConnector);
        host.Children.Add(outConnector);

        Canvas.SetLeft(host, vm.X);
        Canvas.SetTop(host, vm.Y);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NodeViewModel.X))
            {
                Canvas.SetLeft(host, vm.X);
                UpdateConnections();
                UpdateGraphExtent();
            }
            else if (args.PropertyName == nameof(NodeViewModel.Y))
            {
                Canvas.SetTop(host, vm.Y);
                UpdateConnections();
                UpdateGraphExtent();
            }
        };

        bool dragging = false;
        Point dragOffset = default;
        body.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
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
                vm.X = SnapToGrid(vm.X);
                vm.Y = SnapToGrid(vm.Y);
            }

            dragging = false;
            body.ReleaseMouseCapture();
        };

        _nodeVisuals[vm] = body;
        _nodeHosts[vm] = host;

        // A modifier is both a connection source (out) and a drop target (in).
        var outPort = new NodePortViewModel
        {
            Name = "out",
            IsOutput = true,
            Owner = vm,
            Element = outConnector,
            Anchor = new Point(vm.PortX, vm.PortY),
        };
        var inPort = new NodePortViewModel
        {
            Name = "in",
            IsOutput = false,
            Owner = vm,
            Element = inConnector,
            Anchor = new Point(vm.X, vm.PortY),
        };
        _sourcePorts[vm] = outPort;
        _outputPorts[vm] = inPort;

        NodeCanvas.Children.Add(host);
        AttachPortAnchor(outPort, host);
        AttachPortAnchor(inPort, host);
        UpdateEmptyHint();
        UpdateGraphExtent();
    }

    // ---------- Presets ----------

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save routing preset",
            Filter = "Ruse preset (*.ruse.json)|*.ruse.json|JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = "routing.ruse.json",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var preset = BuildPreset();
            System.IO.File.WriteAllText(dialog.FileName, preset.ToJson());
            SetStatus($"Preset saved: {System.IO.Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
    }

    private Preset BuildPreset()
    {
        var preset = new Preset { Name = "Ruse routing" };
        var sourceIndex = new Dictionary<SourceNode, int>();
        var outputIndex = new Dictionary<OutputNode, int>();
        var modifierIndex = new Dictionary<ModifierNode, int>();

        foreach (var (vm, obj) in _nodeBacking)
        {
            switch (obj)
            {
                case SourceNode source:
                    sourceIndex[source] = preset.Sources.Count;
                    preset.Sources.Add(new PresetSource
                    {
                        Title = source.Title,
                        Kind = source.Kind.ToString(),
                        ProcessId = source.ProcessId,
                        DeviceId = source.DeviceId,
                        X = vm.X,
                        Y = vm.Y,
                        Volume = vm.Volume,
                        Muted = vm.Muted,
                    });
                    break;
                case OutputNode output:
                    outputIndex[output] = preset.Outputs.Count;
                    preset.Outputs.Add(new PresetOutput
                    {
                        DeviceId = output.DeviceId,
                        Title = output.Title,
                        IsVirtual = vm.MicHint != null,
                        MicHint = vm.MicHint,
                        X = vm.X,
                        Y = vm.Y,
                        Volume = vm.Volume,
                        Muted = vm.Muted,
                    });
                    break;
                case ModifierNode modifier:
                    modifierIndex[modifier] = preset.Modifiers.Count;
                    preset.Modifiers.Add(new PresetModifier
                    {
                        Title = modifier.Title,
                        X = vm.X,
                        Y = vm.Y,
                        Processors = modifier.Processors.Select(PresetSerializer.SerializeProcessor).ToList(),
                    });
                    break;
            }
        }

        // Modifier wiring (indices resolved against the maps above).
        foreach (var modifier in _graph.Modifiers)
        {
            if (!modifierIndex.TryGetValue(modifier, out int mi))
            {
                continue;
            }

            foreach (var source in modifier.Inputs)
            {
                if (sourceIndex.TryGetValue(source, out int si))
                {
                    preset.SourceModifiers.Add(new PresetSourceModifierLink
                    {
                        SourceIndex = si,
                        ModifierIndex = mi,
                    });
                }
            }

            foreach (var output in modifier.Outputs)
            {
                if (outputIndex.TryGetValue(output, out int oi))
                {
                    preset.ModifierOutputs.Add(new PresetModifierOutputLink
                    {
                        ModifierIndex = mi,
                        OutputIndex = oi,
                    });
                }
            }

            foreach (var next in modifier.Next)
            {
                if (modifierIndex.TryGetValue(next, out int ni))
                {
                    preset.ModifierLinks.Add(new PresetModifierLink
                    {
                        FromModifierIndex = mi,
                        ToModifierIndex = ni,
                    });
                }
            }
        }

        foreach (var output in _graph.Outputs)
        {
            if (!outputIndex.TryGetValue(output, out int oi))
            {
                continue;
            }

            foreach (var route in output.Inputs)
            {
                if (sourceIndex.TryGetValue(route.Source, out int si))
                {
                    preset.Routes.Add(new PresetRoute { SourceIndex = si, OutputIndex = oi, Gain = route.Gain });
                }
            }
        }

        return preset;
    }

    private void OnLoadPresetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load routing preset",
            Filter = "Ruse preset (*.ruse.json)|*.ruse.json|JSON (*.json)|*.json|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var preset = Preset.FromJson(System.IO.File.ReadAllText(dialog.FileName));
            ApplyPreset(preset);
            SetStatus($"Preset loaded: {System.IO.Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Load failed: {ex.Message}");
        }
    }

    private void ApplyPreset(Preset preset)
    {
        OnClearClick(this, new RoutedEventArgs());

        var sourceVms = new List<NodeViewModel>();
        var outputVms = new List<NodeViewModel>();

        foreach (var s in preset.Sources)
        {
            if (!Enum.TryParse<SourceKind>(s.Kind, out var kind))
            {
                kind = SourceKind.Application;
            }

            var source = new SourceNode(s.Title, kind, s.ProcessId, s.DeviceId);
            var vm = AddSourceNode(source, s.X, s.Y);
            if (vm != null)
            {
                vm.Volume = s.Volume;
                vm.Muted = s.Muted;
                source.Gain = s.Volume;
                source.Muted = s.Muted;
                sourceVms.Add(vm);
            }
        }

        foreach (var o in preset.Outputs)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(o.DeviceId);

                NodeViewModel? vm = o.IsVirtual
                    ? AddVirtualOutputNode(
                        VirtualDeviceCatalog.ResolveRender(o.DeviceId, o.Title, o.MicHint), o.X, o.Y)
                    : AddOutputNode(new DeviceItem(device), o.X, o.Y);

                if (vm != null)
                {
                    vm.Volume = o.Volume;
                    vm.Muted = o.Muted;
                    outputVms.Add(vm);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Skipped output '{o.Title}': {ex.Message}");
            }
        }

        foreach (var route in preset.Routes)
        {
            if (route.SourceIndex < 0 || route.SourceIndex >= sourceVms.Count ||
                route.OutputIndex < 0 || route.OutputIndex >= outputVms.Count)
            {
                continue;
            }

            if (_nodeBacking.TryGetValue(sourceVms[route.SourceIndex], out var so) &&
                _nodeBacking.TryGetValue(outputVms[route.OutputIndex], out var oo) &&
                so is SourceNode source && oo is OutputNode output)
            {
                _graph.Connect(source, output, route.Gain);
                AddConnectionVisual(sourceVms[route.SourceIndex], outputVms[route.OutputIndex]);
            }
        }

        // Modifiers, then their wiring, so each link rebuilds the chain correctly.
        var modifierVms = new List<NodeViewModel?>();
        foreach (var m in preset.Modifiers)
        {
            var modifier = new ModifierNode(m.Title);
            foreach (var entry in m.Processors)
            {
                var processor = PresetSerializer.CreateProcessor(entry);
                if (processor != null)
                {
                    modifier.Add(processor);
                }
            }

            if (modifier.Count == 0)
            {
                var fallback = AudioProcessorCatalog.Create(GainProcessor.Id);
                if (fallback != null)
                {
                    modifier.Add(fallback);
                }
            }

            modifierVms.Add(AddModifierNodeCore(modifier, m.Title, m.X, m.Y));
        }

        foreach (var link in preset.SourceModifiers)
        {
            if (TryResolve(sourceVms, link.SourceIndex, out var srcVm) &&
                TryResolve(modifierVms, link.ModifierIndex, out var modVm) &&
                _nodeBacking.TryGetValue(srcVm, out var so) &&
                _nodeBacking.TryGetValue(modVm, out var mo) &&
                so is SourceNode source && mo is ModifierNode modifier)
            {
                _graph.ConnectSourceToModifier(source, modifier);
                AddConnectionVisual(srcVm, modVm);
            }
        }

        foreach (var link in preset.ModifierLinks)
        {
            if (TryResolve(modifierVms, link.FromModifierIndex, out var fromVm) &&
                TryResolve(modifierVms, link.ToModifierIndex, out var toVm) &&
                _nodeBacking.TryGetValue(fromVm, out var fo) &&
                _nodeBacking.TryGetValue(toVm, out var to) &&
                fo is ModifierNode first && to is ModifierNode second)
            {
                _graph.ConnectModifierToModifier(first, second);
                AddConnectionVisual(fromVm, toVm);
            }
        }

        foreach (var link in preset.ModifierOutputs)
        {
            if (TryResolve(modifierVms, link.ModifierIndex, out var modVm) &&
                TryResolve(outputVms, link.OutputIndex, out var outVm) &&
                _nodeBacking.TryGetValue(modVm, out var mo) &&
                _nodeBacking.TryGetValue(outVm, out var oo) &&
                mo is ModifierNode modifier && oo is OutputNode output)
            {
                _graph.ConnectModifierToOutput(modifier, output);
                AddConnectionVisual(modVm, outVm);
            }
        }
    }

    private static bool TryResolve(IReadOnlyList<NodeViewModel?> list, int index, out NodeViewModel vm)
    {
        vm = null!;
        if (index < 0 || index >= list.Count || list[index] == null)
        {
            return false;
        }

        vm = list[index]!;
        return true;
    }

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
        volume.ValueChanged += (_, _) => vm.Volume = (float)volume.Value;

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
            vm.Muted = !vm.Muted;
            muteButton.Content = vm.Muted ? "Unmute" : "Mute";
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
        Point dragOffset = default;
        body.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
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
                vm.X = SnapToGrid(vm.X);
                vm.Y = SnapToGrid(vm.Y);
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

    /// <summary>Highlights the node a dragged cable would connect to.</summary>
    private void SetDropHighlight(NodeViewModel? vm)
    {
        if (ReferenceEquals(_dropHighlight, vm))
        {
            return;
        }

        if (_dropHighlight != null && _nodeVisuals.TryGetValue(_dropHighlight, out var previous))
        {
            previous.BorderBrush = NodeIdleBorder;
            previous.BorderThickness = new Thickness(1);
        }

        _dropHighlight = vm;

        if (vm != null && _nodeVisuals.TryGetValue(vm, out var body))
        {
            body.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(vm.Accent));
            body.BorderThickness = new Thickness(2);
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

        var cableColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4C8DFF"));

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

        // The travelling waveform that shows audio flowing along the cable.
        var waveFigure = new PathFigure { StartPoint = fromPort.Anchor };
        var waveSegment = new PolyLineSegment();
        for (int i = 0; i <= WaveSegments; i++)
        {
            waveSegment.Points.Add(fromPort.Anchor);
        }

        waveFigure.Segments.Add(waveSegment);
        var wave = new Path
        {
            Data = new PathGeometry { Figures = { waveFigure } },
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BFD8FF")),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0.95,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        conn.Wave = wave;
        conn.WaveFigure = waveFigure;
        conn.WaveSegment = waveSegment;

        _connections.Add(conn);
        ConnectionCanvas.Children.Add(cable);
        ConnectionCanvas.Children.Add(hitPath);
        ConnectionCanvas.Children.Add(wave);
        UpdateConnections();
    }

    /// <summary>Number of straight segments used to draw a cable's waveform.</summary>
    private const int WaveSegments = 48;

    /// <summary>How far the waveform can swing from the cable, in pixels.</summary>
    private const double WaveAmplitude = 7;

    private readonly System.Diagnostics.Stopwatch _waveClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Draws the travelling waveform on every cable that is carrying audio.
    /// Runs on the render tick so the motion is smooth; the geometry is
    /// updated in place, so nothing is allocated per frame.
    /// </summary>
    private void OnRender(object? sender, EventArgs e)
    {
        if (_connections.Count == 0)
        {
            return;
        }

        double time = _waveClock.Elapsed.TotalSeconds;

        foreach (var conn in _connections)
        {
            var wave = conn.Wave;
            var segment = conn.WaveSegment;
            if (wave == null || segment == null)
            {
                continue;
            }

            float level = LevelForConnection(conn);
            conn.Level = level;

            if (level <= 0.004f)
            {
                if (wave.Visibility != Visibility.Collapsed)
                {
                    wave.Visibility = Visibility.Collapsed;
                }

                continue;
            }

            if (wave.Visibility != Visibility.Visible)
            {
                wave.Visibility = Visibility.Visible;
            }

            Point start = conn.Start;
            Point end = conn.End;
            var (c1, c2) = CableControls(start, end);

            double amplitude = WaveAmplitude * Math.Clamp(level * 1.8, 0, 1);
            double phase = time * 7.5;

            var points = segment.Points;
            for (int i = 0; i <= WaveSegments && i < points.Count; i++)
            {
                double u = i / (double)WaveSegments;
                Point p = CubicPoint(start, c1, c2, end, u);
                var (dx, dy) = CubicTangent(start, c1, c2, end, u);
                double len = Math.Sqrt((dx * dx) + (dy * dy));
                if (len < 0.0001)
                {
                    len = 1;
                }

                // Normal of the curve, so the wave always crosses the cable.
                double nx = -dy / len;
                double ny = dx / len;

                // Taper to zero at both ends so the wave stays inside the ports.
                double taper = Math.Sin(Math.PI * u);
                double offset = Math.Sin((u * Math.PI * 6) + phase) * amplitude * taper;

                points[i] = new Point(p.X + (nx * offset), p.Y + (ny * offset));
            }
        }
    }

    /// <summary>
    /// Signal level on a cable: the source's own level, or — for a cable that
    /// leaves a modifier — the level of the source feeding that chain.
    /// </summary>
    private float LevelForConnection(ConnectionViewModel conn)
    {
        if (_nodeBacking.TryGetValue(conn.From.Owner, out var fromObj) && fromObj is SourceNode source)
        {
            return source.MeterLevel;
        }

        return UpstreamLevel(conn.From.Owner, 0);
    }

    private float UpstreamLevel(NodeViewModel node, int depth)
    {
        if (depth > 8)
        {
            return 0f;
        }

        foreach (var c in _connections)
        {
            if (!ReferenceEquals(c.To.Owner, node))
            {
                continue;
            }

            if (_nodeBacking.TryGetValue(c.From.Owner, out var obj) && obj is SourceNode source)
            {
                return source.MeterLevel;
            }

            float deeper = UpstreamLevel(c.From.Owner, depth + 1);
            if (deeper > 0f)
            {
                return deeper;
            }
        }

        return 0f;
    }

    /// <summary>Removes a single link between two nodes (any supported pairing).</summary>
    private void RemoveConnection(ConnectionViewModel conn)
    {
        if (_nodeBacking.TryGetValue(conn.From.Owner, out var fromObj) &&
            _nodeBacking.TryGetValue(conn.To.Owner, out var toObj))
        {
            switch (fromObj, toObj)
            {
                case (SourceNode source, OutputNode output):
                    _graph.Disconnect(source, output);
                    break;
                case (SourceNode source, ModifierNode modifier):
                    _graph.DisconnectSourceFromModifier(source, modifier);
                    break;
                case (ModifierNode modifier, OutputNode output):
                    _graph.DisconnectModifierFromOutput(modifier, output);
                    break;
                case (ModifierNode first, ModifierNode second):
                    _graph.DisconnectModifierFromModifier(first, second);
                    break;
            }
        }

        if (conn.Cable != null)
        {
            ConnectionCanvas.Children.Remove(conn.Cable);
        }

        if (conn.HitPath != null)
        {
            ConnectionCanvas.Children.Remove(conn.HitPath);
        }

        _connections.Remove(conn);
    }

    /// <summary>Renames a node (display name + backing audio node title).</summary>
    private void RenameNode(NodeViewModel vm)
    {
        string? name = Ui.InputDialog.Show(this, "Rename node", "Node name", vm.Title);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = name.Trim();
        vm.Title = name;

        if (_nodeBacking.TryGetValue(vm, out var obj))
        {
            switch (obj)
            {
                case SourceNode source:
                    source.Title = name;
                    break;
                case OutputNode output:
                    output.Title = name;
                    break;
                case ModifierNode modifier:
                    modifier.Title = name;
                    break;
            }
        }

        SetStatus($"Renamed node to '{name}'.");
    }

    /// <summary>Opens the gain prompt for a cable and applies the result to its route.</summary>
    private void PromptRouteGain(ConnectionViewModel conn)
    {
        float? gain = Ui.GainDialog.Show(this, $"Gain: {conn.From.Owner.Title} → {conn.To.Owner.Title}", conn.Gain);
        if (gain == null)
        {
            return;
        }

        ApplyRouteGain(conn, gain.Value);
        SetStatus($"Route gain set to {(int)Math.Round(gain.Value * 100)}%.");
    }

    private void ApplyRouteGain(ConnectionViewModel conn, float gain)
    {
        conn.Gain = gain;

        if (_nodeBacking.TryGetValue(conn.From.Owner, out var fromObj) &&
            _nodeBacking.TryGetValue(conn.To.Owner, out var toObj) &&
            fromObj is SourceNode source && toObj is OutputNode output)
        {
            var route = output.Inputs.FirstOrDefault(r => ReferenceEquals(r.Source, source));
            if (route != null)
            {
                route.Gain = gain;
                output.RefreshRoutes();
            }
        }

        // Thicker/dimmer cable hints at a boosted/cut route.
        if (conn.Cable != null)
        {
            conn.Cable.StrokeThickness = 1.5 + (gain * 2.5);
            conn.Cable.Opacity = 0.5 + (Math.Min(gain, 1f) * 0.4);
        }
    }

    /// <summary>Removes every route attached to the given node.</summary>
    private void DisconnectNode(NodeViewModel vm)
    {
        var affected = _connections
            .Where(c => c.From.Owner == vm || c.To.Owner == vm)
            .ToList();

        foreach (var conn in affected)
        {
            RemoveConnection(conn);
        }

        SetStatus(affected.Count > 0
            ? $"Disconnected {affected.Count} route(s) from '{vm.Title}'."
            : $"'{vm.Title}' had no connections.");
    }

    /// <summary>Removes a node and every route attached to it.</summary>
    private void RemoveNode(NodeViewModel vm)
    {
        DisconnectNode(vm);

        if (_nodeHosts.TryGetValue(vm, out var host))
        {
            NodeCanvas.Children.Remove(host);
            _nodeHosts.Remove(vm);
        }

        _nodeVisuals.Remove(vm);

        if (_nodeBacking.TryGetValue(vm, out var backing))
        {
            switch (backing)
            {
                case SourceNode source:
                    _graph.RemoveSource(source);
                    break;
                case OutputNode output:
                    _graph.RemoveOutput(output);
                    break;
                case ModifierNode modifier:
                    _graph.RemoveModifier(modifier);
                    break;
            }

            _nodeBacking.Remove(vm);
        }

        _sourcePorts.Remove(vm);
        _outputPorts.Remove(vm);
        _nodes.Remove(vm);
        UpdateEmptyHint();
        UpdateGraphExtent();
        SetStatus($"Removed node '{vm.Title}'.");
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = _nodeBacking.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Self-test for the connection affordance: a cable dropped anywhere on a
    /// node's body must find that node, including on nodes that are much
    /// taller than their nominal height (a modifier with many parameters).
    /// </summary>
    public string RunDropTargetSelfTest()
    {
        var modifier = new ModifierNode("Tall test");
        modifier.Add(new GainProcessor { Gain = 0.8f });
        modifier.Add(new HighPassProcessor { Cutoff = 120 });

        var vm = AddModifierNodeCore(modifier, "Tall test", 120, 120);
        if (vm == null)
        {
            return "could not create the test node -> FAIL";
        }

        UpdateLayout();

        if (!_nodeVisuals.TryGetValue(vm, out var body))
        {
            return "test node has no visual -> FAIL";
        }

        double realHeight = body.ActualHeight;
        double nominal = vm.Height;
        Point origin = body.TranslatePoint(new Point(0, 0), ConnectionCanvas);

        // Middle of the body, and a point near its bottom edge.
        Point middle = new Point(origin.X + (body.ActualWidth / 2), origin.Y + (body.ActualHeight / 2));
        Point low = new Point(origin.X + 20, origin.Y + realHeight - 12);

        var hitMiddle = FindDropTargetAt(middle);
        var hitLow = FindDropTargetAt(low);
        var hitOrigin = FindDropTargetAt(new Point(origin.X + 5, origin.Y + 5));

        // A point far below the node must not match anything.
        var miss = FindDropTargetAt(new Point(origin.X + 20, origin.Y + realHeight + 200));

        string layout = $"vm=({vm.X:0},{vm.Y:0}) origin=({origin.X:0},{origin.Y:0}) " +
                        $"size=({body.ActualWidth:0}x{body.ActualHeight:0}) " +
                        $"nodes={_nodes.Count} canvas={ConnectionCanvas.ActualWidth:0}x{ConnectionCanvas.ActualHeight:0}";

        bool pass = ReferenceEquals(hitMiddle, vm)
            && ReferenceEquals(hitLow, vm)
            && miss == null
            && realHeight > nominal + 40;

        string drop = $"Tall-node drop: {layout} | origin-hit -> {(ReferenceEquals(hitOrigin, vm) ? vm.Title : "miss")} | " +
                      $"centre -> {(ReferenceEquals(hitMiddle, vm) ? vm.Title : "miss")} | " +
                      $"bottom -> {(ReferenceEquals(hitLow, vm) ? vm.Title : "miss")} | " +
                      $"below -> {(miss == null ? "miss" : miss.Title)} -> {(pass ? "PASS" : "FAIL")}";

        // ---- Zoom ----
        UpdateGraphExtent();
        double hostWidthBefore = GraphHost.Width;

        var pointer = new Point(320, 180);
        double worldBefore = (GraphScroll.HorizontalOffset + pointer.X) / _zoomTransform.ScaleX;

        ZoomAt(pointer, 1.25);
        double zoomedScale = _zoomTransform.ScaleX;

        // The ScrollViewer applies the anchored offset on the next dispatcher
        // pass, so let that happen before measuring.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);

        double worldAfter = (GraphScroll.HorizontalOffset + pointer.X) / _zoomTransform.ScaleX;

        string zoomDiag = $"diag[host={GraphHost.Width:0} layer={GraphLayer.Width:0} " +
                          $"extent={GraphScroll.ExtentWidth:0} viewport={GraphScroll.ViewportWidth:0} " +
                          $"offset={GraphScroll.HorizontalOffset:0}]";

        // Zooming out must reveal more of the world, and the canvas must be
        // bigger than the nodes so the user can pan around.
        double worldWidth = GraphLayer.Width;
        bool roomToPan = worldWidth > (vm.X + vm.Width) + 500;

        ZoomAt(pointer, 1 / 1.25);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
        double restoredScale = _zoomTransform.ScaleX;

        bool zoomPass = Math.Abs(zoomedScale - 1.25) < 0.01
            && Math.Abs(worldAfter - worldBefore) < 2
            && Math.Abs(restoredScale - 1.0) < 0.01
            && roomToPan
            && GraphHost.Width > 0;

        string zoom = $"Zoom: scale {zoomedScale:0.00} -> restored {restoredScale:0.00}, " +
                      $"pointer stayed on world x {worldBefore:0.0} -> {worldAfter:0.0}, " +
                      $"world width {worldWidth:0} (host was {hostWidthBefore:0}), room to pan {roomToPan} " +
                      $"{zoomDiag} -> {(zoomPass ? "PASS" : "FAIL")}";

        // ---- Click feedback ----
        _sounds.PlayClick();
        bool clickPass = _sounds.DeviceReady && _sounds.Enabled;

        string click = $"Click sound: device ready {_sounds.DeviceReady}, enabled {_sounds.Enabled} " +
                       $"-> {(clickPass ? "PASS" : "FAIL")}";

        return $"{drop} | {zoom} | {click}";
    }

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
            double height = vm.Height;
            if (_nodeVisuals.TryGetValue(vm, out var body) && body.ActualHeight > 0)
            {
                height = body.ActualHeight;
            }

            contentWidth = Math.Max(contentWidth, vm.X + vm.Width);
            contentHeight = Math.Max(contentHeight, vm.Y + height);
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
    private const double CanvasMargin = 1400;

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
    /// because the world is scaled by a layout transform.
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

            if (conn.Wave != null)
            {
                conn.Wave.StrokeThickness = WaveThickness / scale;
            }
        }

        if (_dragLine != null)
        {
            _dragLine.StrokeThickness = thickness;
        }
    }

    private const double CableThickness = 2.5;
    private const double CableHitThickness = 16;
    private const double WaveThickness = 2;

    private void OnGraphResized(object sender, SizeChangedEventArgs e) => UpdateGraphExtent();

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
            body.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2F3D"));
            body.BorderThickness = new Thickness(1);
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

    // ---------- Meters ----------

    private readonly Dictionary<NodeViewModel, double> _meterSmoothed = new();

    // Cached brushes — recolouring a bar must not allocate a new brush each tick.
    private static readonly SolidColorBrush MeterLowBrush = Freeze(Color.FromRgb(0x39, 0xD9, 0x8A));
    private static readonly SolidColorBrush MeterMidBrush = Freeze(Color.FromRgb(0xE0, 0xC2, 0x3A));
    private static readonly SolidColorBrush MeterHighBrush = Freeze(Color.FromRgb(0xE0, 0x60, 0x3A));

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Draws a per-node volume bar from the live audio level. Fast attack makes
    /// the bar jump on transients; slow release makes it fall smoothly so it
    /// reads like a real VU meter.
    /// </summary>
    private void UpdateMeters()
    {
        double worstLatency = 0;
        bool anyRunning = false;
        bool anyClipping = false;
        bool anyLowLatency = false;

        bool demo = _demoLevels.Count > 0;

        foreach (var (vm, obj) in _nodeBacking)
        {
            float level;
            switch (obj)
            {
                case SourceNode s:
                    level = s.MeterLevel;
                    anyRunning |= s.IsRunning;
                    break;
                case OutputNode o:
                    level = o.MeterLevel;
                    anyRunning |= o.IsRunning;
                    anyClipping |= o.Clipping;
                    if (o.IsRunning)
                    {
                        anyLowLatency |= o.LowLatencyActive;
                        double lat = o.EstimatedLatencyMs;
                        if (lat > worstLatency)
                        {
                            worstLatency = lat;
                        }
                    }
                    break;
                default:
                    level = 0f;
                    break;
            }

            if (demo)
            {
                // Synthetic, gently wavering levels so the bars animate.
                _demoLevels.TryGetValue(vm, out float baseLevel);
                if (baseLevel == 0f)
                {
                    baseLevel = (float)(0.2 + _demoRng.NextDouble() * 0.6);
                    _demoLevels[vm] = baseLevel;
                }

                double wobble = 0.75 + 0.25 * Math.Sin(Environment.TickCount / 220.0 + vm.GetHashCode());
                level = (float)(baseLevel * wobble);
            }

            // Perceptual scaling: a raw RMS of 0.3 should look "loud-ish".
            double target = Math.Min(1.0, Math.Pow(level, 0.6));

            _meterSmoothed.TryGetValue(vm, out double current);

            // Fast attack (rise quickly) / slow release (decay gently).
            double smoothed = target > current
                ? current + (target - current) * 0.55
                : current + (target - current) * 0.12;

            _meterSmoothed[vm] = smoothed;

            if (double.IsNaN(smoothed) || smoothed < 0.001)
            {
                smoothed = 0;
            }

            if (_nodeVisuals.TryGetValue(vm, out var body) &&
                body.Tag is Rectangle bar && bar.RenderTransform is ScaleTransform scale)
            {
                if (Math.Abs(scale.ScaleX - smoothed) > 0.001)
                {
                    scale.ScaleX = smoothed;
                }

                // Green → amber → red as the signal approaches clipping.
                SolidColorBrush color = smoothed switch
                {
                    < 0.7 => MeterLowBrush,
                    < 0.9 => MeterMidBrush,
                    _ => MeterHighBrush,
                };

                if (!ReferenceEquals(bar.Fill, color))
                {
                    bar.Fill = color;
                }
            }
        }

        // Latency meter: fill relative to a 60ms full-scale, colour-shifted as it grows.
        // Display-only; reuses this existing tick so it costs nothing extra.
        double latencyFraction = Math.Clamp(worstLatency / 60.0, 0, 1);
        LatencyMeterScale.ScaleX = latencyFraction;
        LatencyMeterBar.Background = worstLatency switch
        {
            <= 0 => MeterLowBrush,
            < 20 => MeterLowBrush,
            < 40 => MeterMidBrush,
            _ => MeterHighBrush,
        };

        LatencyText.Text = worstLatency > 0 ? $"~{worstLatency:0} ms" : "";

        // Low-latency chip: show only when at least one live output has it active.
        LowLatencyChip.Visibility = anyLowLatency ? Visibility.Visible : Visibility.Collapsed;

        // Peak-hold the clip indicator so a brief overload stays visible ~1s.
        if (anyClipping)
        {
            _clipHoldUntil = Environment.TickCount64 + 1000;
        }

        ClipIndicator.Visibility = Environment.TickCount64 < _clipHoldUntil
            ? Visibility.Visible
            : Visibility.Collapsed;

        SetRunState(anyRunning);
    }

    private long _clipHoldUntil;

    /// <summary>Shows a message in the status bar (also used by diagnostics).</summary>
    public void ShowStatus(string text) => SetStatus(text);

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    protected override void OnClosed(EventArgs e)
    {
        _meterTimer.Stop();
        _refreshTimer.Stop();
        _graph.Dispose();
        base.OnClosed(e);
    }
}
