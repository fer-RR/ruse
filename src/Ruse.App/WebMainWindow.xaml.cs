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

        if (_controller.UserSettings.MinimizeToTray)
        {
            (string open, string exit) = TrayText(_controller.UserSettings.UiLanguage);
            _tray = new TrayIcon(this, open, exit);
            _tray.OpenRequested += RestoreFromTray;
            _tray.ExitRequested += ExitFromTray;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState is WindowState.Normal or WindowState.Maximized)
        {
            _lastVisibleState = WindowState;
            _hiddenToTray = false;
            return;
        }

        if (WindowState != WindowState.Minimized ||
            !_controller.UserSettings.MinimizeToTray ||
            _tray == null ||
            _hiddenToTray)
        {
            return;
        }

        _hiddenToTray = true;
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = _lastVisibleState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        _hiddenToTray = false;
        Activate();
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
