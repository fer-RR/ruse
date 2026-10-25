using System.Windows;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>Thin native host for the React/WebView2 workspace.</summary>
public partial class WebMainWindow : Window
{
    private readonly WebShellController _controller;
    private bool _closed;

    public WebMainWindow()
    {
        InitializeComponent();
        _controller = new WebShellController(this);
        Loaded += OnLoaded;
        Closing += (_, _) => _controller.Shutdown();
        Closed += (_, _) =>
        {
            _closed = true;
            _controller.Dispose();
        };
    }

    internal string RunParitySelfTest() => _controller.RunParitySelfTest();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowFrameBehavior.Attach(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _controller.InitializeAsync(WebView);
        }
        catch (Exception ex)
        {
            AppLog.Error("The Web UI could not start; opening the legacy shell.", ex);
            if (_closed)
            {
                return;
            }

            var legacy = new MainWindow();
            Application.Current.MainWindow = legacy;
            legacy.Show();
            Close();
        }
    }
}
