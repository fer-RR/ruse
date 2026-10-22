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

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _meterTimer.Tick += (_, _) => UpdateMeters();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _refreshTimer.Tick += (_, _) => RefreshApps();
        _refreshTimer.Start();

        GraphScroll.SizeChanged += OnGraphResized;
        RefreshChains();

        // Apply the saved appearance options.
        _history.Changed += UpdateHistoryButtons;
        UpdateHistoryButtons();

        // The dotted canvas background is a cached bitmap rather than a live
        // drawing brush: a brush re-renders its geometry for every tile on
        // every repaint, which is expensive in software rendering.
        GraphHost.Background = CreateDotTileBrush();

        Activated += (_, _) =>
        {
            _refreshOnActivate = true;
            RefreshApps();
        };

        // The whole graph world scales through this one transform, so the
        // cables and the nodes always stay aligned at any zoom level.
        GraphLayer.LayoutTransform = _zoomTransform;
        UpdateZoomIndicator();

        Loaded += (_, _) =>
        {
            _startupMs = _startupClock.Elapsed.TotalMilliseconds;
            RestoreWindowBounds();
            RefreshAllDevices();
            RefreshApps();
            _meterTimer.Start();

            // Anything that goes wrong on the UI thread is logged and shown,
            // instead of vanishing into the debugger.
            Dispatcher.UnhandledException += (_, e) =>
            {
                AppLog.Error("Unhandled exception on the UI thread.", e.Exception);
                ShowAlert("Something went wrong: " + e.Exception.Message, isError: true);
                UpdateErrorIndicator();
                e.Handled = true;
            };

            CheckRenderingTier();

            if (App.DemoMode)
            {
                BuildDemoGraph();
            }
            else if (!App.UiTestMode && !App.LiveDemoMode && !App.PerfTestMode && _settings.AutoLoadSession && System.IO.File.Exists(AppSettings.SessionPath))
            {
                TryLoadSession();
            }

            UpdateGraphExtent();
        };

        Closing += (_, _) =>
        {
            SaveWindowState();
            _sounds.Dispose();
        };
    }

    private readonly AppSettings _settings;

    /// <summary>
    /// Builds the dotted canvas background once, as a frozen bitmap tile.
    /// A <see cref="DrawingBrush"/> would re-render the dot geometry for every
    /// tile on every repaint; a bitmap is blitted instead.
    /// </summary>
    private static ImageBrush CreateDotTileBrush()
    {
        const int TileSize = 24;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x15)), null, new Rect(0, 0, TileSize, TileSize));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x1C, 0x22, 0x30)), null, new Point(0.75, 0.75), 0.75, 0.75);
        }

        var tile = new RenderTargetBitmap(TileSize, TileSize, 96, 96, PixelFormats.Pbgra32);
        tile.Render(visual);
        tile.Freeze();

        var brush = new ImageBrush(tile)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, TileSize, TileSize),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };

        brush.Freeze();
        return brush;
    }

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
            AppLog.Error("Could not restore the last session.", ex);
            ShowAlert("Could not restore the last session: " + ex.Message, isError: true);
            UpdateErrorIndicator();
        }
    }
}

