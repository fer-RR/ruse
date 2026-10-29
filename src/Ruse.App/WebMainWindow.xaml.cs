using System.ComponentModel;
using System.Windows;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>Thin native host for the React/WebView2 workspace.</summary>
public partial class WebMainWindow : Window
{
    private readonly WebShellController _controller;
    private bool _closed;
    private bool _closeConfirmed;
    private bool _hiddenToTray;
    private bool _trayPrompting;
    private TrayIcon? _tray;
    private WindowState _lastVisibleState = WindowState.Normal;

    public WebMainWindow()
    {
        InitializeComponent();
        _controller = new WebShellController(this);
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _tray?.Dispose();
            _tray = null;
            _controller.Dispose();
        };
    }

    internal string RunParitySelfTest() => _controller.RunParitySelfTest();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_controller.HasActiveRecording)
        {
            _controller.Shutdown();
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            "A recording is still in progress. Closing Ruse will stop and save it. Close anyway?",
            "Ruse", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _closeConfirmed = true;
        _controller.Shutdown();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowFrameBehavior.Attach(this);
    }

    private void CreateTrayIcon()
    {
        if (!_controller.UserSettings.MinimizeToTray ||
            !_controller.UserSettings.TrayPromptShown ||
            _tray != null)
        {
            return;
        }

        (string open, string exit) = TrayText(_controller.UserSettings.UiLanguage);
        _tray = new TrayIcon(this, open, exit);
        _tray.OpenRequested += RestoreFromTray;
        _tray.ExitRequested += ExitFromTray;
    }

    /// <summary>Creates or removes the tray icon after a settings change.</summary>
    internal void SyncTrayIcon()
    {
        if (_controller.UserSettings.MinimizeToTray && _controller.UserSettings.TrayPromptShown)
        {
            CreateTrayIcon();
            return;
        }

        _tray?.Dispose();
        _tray = null;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState is WindowState.Normal or WindowState.Maximized)
        {
            _lastVisibleState = WindowState;
            _hiddenToTray = false;
            return;
        }

        if (WindowState != WindowState.Minimized || _hiddenToTray || _trayPrompting)
        {
            return;
        }

        if (!_controller.UserSettings.TrayPromptShown)
        {
            _trayPrompting = true;
            bool minimizeToTray = MessageBox.Show(
                "Minimize Ruse to the notification area?\n\nAudio keeps routing while the window is hidden. You can change this later in Settings.",
                "Ruse",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
            _trayPrompting = false;

            _controller.SaveTrayPreference(minimizeToTray);
            if (!minimizeToTray)
            {
                return;
            }

            CreateTrayIcon();
        }

        if (!_controller.UserSettings.MinimizeToTray || _tray == null)
        {
            return;
        }

        _hiddenToTray = true;
        Hide();
        ShowInTaskbar = false;
    }

    internal void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = _lastVisibleState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        _hiddenToTray = false;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitFromTray()
    {
        if (_hiddenToTray)
        {
            RestoreFromTray();
        }

        Close();
    }

    private static (string Open, string Exit) TrayText(string language) => language switch
    {
        "pt-BR" => ("Abrir Ruse", "Sair"),
        "es" => ("Abrir Ruse", "Salir"),
        "fr" => ("Ouvrir Ruse", "Quitter"),
        "de" => ("Ruse öffnen", "Beenden"),
        "it" => ("Apri Ruse", "Esci"),
        "ja" => ("Ruse を開く", "終了"),
        "ko" => ("Ruse 열기", "종료"),
        "zh-CN" => ("打开 Ruse", "退出"),
        _ => ("Open Ruse", "Exit"),
    };

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _controller.InitializeAsync(WebView);
            CreateTrayIcon();
        }
        catch (Exception ex)
        {
            AppLog.Error("The Web UI could not start.", ex);
            if (_closed)
            {
                return;
            }

            string detail =
                "The WebView2 runtime may be missing or damaged.\n\n" +
                ex.GetBaseException().Message +
                "\n\nDetails were written to:\n" + AppLog.LogPath;
            var dialog = new StartupErrorDialog(
                "Ruse could not open its interface. Install the Microsoft Edge WebView2 Runtime, then start Ruse again.",
                detail)
            {
                Owner = this,
            };
            dialog.ShowDialog();
            Close();
            Application.Current.Shutdown();
        }
    }
}
