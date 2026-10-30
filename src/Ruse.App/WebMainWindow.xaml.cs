using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>Thin native host for the React/WebView2 workspace.</summary>
public partial class WebMainWindow : Window
{
    private readonly WebShellController _controller;
    private bool _closed;
    private bool _closeConfirmed;
    private bool _hiddenToTray;
    private bool _exitFromTray;
    private bool _trayPromptOpen;
    private TrayIcon? _tray;
    private WindowState _lastVisibleState = WindowState.Normal;

    public WebMainWindow()
    {
        InitializeComponent();
        _controller = new WebShellController(this);
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        Application.Current.SessionEnding += (_, _) => _exitFromTray = true;
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
        if (_closeConfirmed)
        {
            _controller.Shutdown();
            return;
        }

        // The window X hides to the tray when the user asked for it; the tray
        // menu's Exit sets _exitFromTray so it always quits for real. Headless
        // self-test windows have no handle and must just shut down.
        if (!_exitFromTray && IsLoaded)
        {
            if (!_controller.UserSettings.TrayPromptShown)
            {
                e.Cancel = true;
                if (_trayPromptOpen)
                {
                    return;
                }

                _trayPromptOpen = true;
                if (!_controller.RequestTrayDecision(ApplyTrayDecision))
                {
                    bool closeToTray = MessageBox.Show(
                        "Keep Ruse running in the notification area when the window is closed?\n\n" +
                        "Audio keeps routing while it is hidden. You can change this later in Settings.",
                        "Ruse",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) == MessageBoxResult.Yes;
                    ApplyTrayDecision(closeToTray);
                }

                return;
            }

            if (_controller.UserSettings.MinimizeToTray)
            {
                CreateTrayIcon();
                if (_tray != null)
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }
            }
        }

        if (!_controller.HasActiveRecording)
        {
            _controller.Shutdown();
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            "A recording is still in progress. Closing Ruse will stop and save it. Close anyway?",
            "Ruse", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            _exitFromTray = false;
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
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            return;
        }

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
        }
    }

    private void HideToTray()
    {
        _hiddenToTray = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void ApplyTrayDecision(bool keep)
    {
        _trayPromptOpen = false;
        _controller.SaveTrayPreference(keep);
        if (keep)
        {
            CreateTrayIcon();
            if (_tray != null)
            {
                HideToTray();
            }

            return;
        }

        _exitFromTray = true;
        Close();
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
        _exitFromTray = true;
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
