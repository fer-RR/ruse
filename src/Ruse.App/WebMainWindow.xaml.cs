using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
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
    private DispatcherTimer? _trayTimer;
    private bool _lastRecording;
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
            _trayTimer?.Stop();
            _trayTimer = null;
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

        if ((!_controller.UserSettings.MinimizeToTray &&
             !_controller.UserSettings.StartMinimized &&
             !_controller.UserSettings.MinimizeToTrayOnMinimize) ||
            !_controller.UserSettings.TrayPromptShown ||
            _tray != null)
        {
            return;
        }

        _tray = new TrayIcon(this, TrayText(_controller.UserSettings.UiLanguage));
        _tray.OpenRequested += RestoreFromTray;
        _tray.ExitRequested += ExitFromTray;
        _tray.ToggleRoutingRequested += ToggleRoutingFromTray;
        _tray.ToggleMicRequested += ToggleMicFromTray;
        _tray.ToggleMasterRequested += ToggleMasterFromTray;
        _tray.MixerRequested += OpenMixerFromTray;
        _tray.TimelineRequested += OpenTimelineFromTray;
        UpdateTray();
        StartTrayTimer();
    }

    /// <summary>Creates or removes the tray icon after a settings change.</summary>
    internal void SyncTrayIcon()
    {
        if ((_controller.UserSettings.MinimizeToTray ||
             _controller.UserSettings.StartMinimized ||
             _controller.UserSettings.MinimizeToTrayOnMinimize) &&
            _controller.UserSettings.TrayPromptShown)
        {
            CreateTrayIcon();
            return;
        }

        StopTrayTimer();
        _tray?.Dispose();
        _tray = null;
    }

    private void StartTrayTimer()
    {
        if (_trayTimer != null)
        {
            return;
        }

        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trayTimer.Tick += (_, _) => UpdateTray();
        _trayTimer.Start();
    }

    private void StopTrayTimer()
    {
        _trayTimer?.Stop();
        _trayTimer = null;
    }

    private void UpdateTray()
    {
        if (_tray == null)
        {
            return;
        }

        TraySnapshot snapshot = _controller.GetTraySnapshot();
        if (_hiddenToTray && snapshot.Recording != _lastRecording)
        {
            (string started, string stopped) = RecordingText(_controller.UserSettings.UiLanguage);
            _tray.ShowNotification("Ruse", snapshot.Recording ? started : stopped);
        }

        _lastRecording = snapshot.Recording;
        _tray.Update(snapshot);
    }

    private void ToggleRoutingFromTray()
    {
        _controller.ToggleRoutingFromTray();
        UpdateTray();
    }

    private void ToggleMicFromTray()
    {
        _controller.ToggleMicMuteFromTray();
        UpdateTray();
    }

    private void ToggleMasterFromTray()
    {
        _controller.ToggleMasterMuteFromTray();
        UpdateTray();
    }

    private void OpenMixerFromTray()
    {
        RestoreFromTray();
        _controller.OpenMixerFromTray();
    }

    private void OpenTimelineFromTray()
    {
        RestoreFromTray();
        _controller.OpenTimelineFromTray();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState is WindowState.Normal or WindowState.Maximized)
        {
            _lastVisibleState = WindowState;
            _hiddenToTray = false;
        }
        else if (WindowState == WindowState.Minimized &&
                 _controller.UserSettings.MinimizeToTrayOnMinimize)
        {
            CreateTrayIcon();
            if (_tray != null)
            {
                HideToTray();
            }
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

    private static (string Started, string Stopped) RecordingText(string language) => language switch
    {
        "de" => ("Aufnahme gestartet", "Aufnahme beendet"),
        "es" => ("Grabación iniciada", "Grabación detenida"),
        "fr" => ("Enregistrement démarré", "Enregistrement arrêté"),
        "it" => ("Registrazione avviata", "Registrazione fermata"),
        "ja" => ("録音を開始しました", "録音を停止しました"),
        "ko" => ("녹음이 시작되었습니다", "녹음이 중지되었습니다"),
        "pt-BR" => ("Gravação iniciada", "Gravação parada"),
        "zh-CN" => ("录音已开始", "录音已停止"),
        _ => ("Recording started", "Recording stopped"),
    };
    private static TrayLabels TrayText(string language) => language switch
    {
        "de" => new("Ruse öffnen", "Beenden", "Routing starten", "Routing stoppen", "Mikrofone stumm schalten", "Mikrofone stummgeschaltet", "Schalten Sie alle Ausgänge stumm", "Stummschaltung aller Ausgänge aufheben", "Mixer", "Zeitleiste", "Aufnahme", "Leerlauf", "Routing"),
        "es" => new("Abrir Ruse", "Salir", "Iniciar enrutamiento", "Detener enrutamiento", "Silenciar micrófonos", "Micrófonos silenciados", "Silenciar todas las salidas", "Desenmudecer todas las salidas", "Mezclador", "Línea de tiempo", "Grabación", "inactivo", "Enrutamiento"),
        "fr" => new("Ouvrir Ruse", "Quitter", "Démarrer le routage", "Arrêter le routage", "Couper les micros", "Micros coupés", "Couper toutes les sorties", "Réactiver toutes les sorties", "Mixeur", "Chronologie", "Enregistrement", "Inactif", "Routage"),
        "it" => new("Apri Ruse", "Esci", "Avvia routing", "Interrompi routing", "Microfoni muti", "Microfoni disattivati", "Disattiva tutte le uscite", "Riattiva tutte le uscite", "Mixer", "Sequenza temporale", "Registrazione", "Inattivo", "Routing"),
        "ja" => new("Ruse を開く", "終了", "ルーティングを開始", "ルーティングを停止", "マイクをミュートする", "マイクがミュートされています", "すべての出力をミュートする", "すべての出力のミュートを解除する", "ミキサー", "タイムライン", "録音", "アイドル状態", "ルーティング"),
        "ko" => new("Ruse 열기", "종료", "라우팅 시작", "라우팅 중지", "마이크 음소거", "마이크가 음소거되었습니다.", "모든 출력을 음소거", "모든 출력 음소거 해제", "믹서", "타임라인", "녹음", "유휴", "라우팅"),
        "pt-BR" => new("Abrir Ruse", "Sair", "Iniciar roteamento", "Parar roteamento", "Silenciar microfones", "Microfones mudos", "Silenciar todas as saídas", "Reativar todas as saídas", "Mixer", "Linha do tempo", "Gravação", "Ocioso", "Roteamento"),
        "zh-CN" => new("打开 Ruse", "退出", "开始路由", "停止路由", "麦克风静音", "麦克风静音", "将所有输出静音", "取消所有输出静音", "混音器", "时间线", "录音", "空闲", "路由"),
        _ => new("Open Ruse", "Exit", "Start routing", "Stop routing", "Mute mics", "Mics muted", "Mute all outputs", "Unmute all outputs", "Mixer", "Timeline", "Recording", "Idle", "Routing"),
    };

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        string? runtimeVersion = null;
        try
        {
            runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            AppLog.Error("The WebView2 runtime was not found.", ex);
        }

        if (string.IsNullOrWhiteSpace(runtimeVersion))
        {
            if (_closed)
            {
                return;
            }

            var missingDialog = new StartupErrorDialog(
                "Ruse needs the Microsoft Edge WebView2 Runtime, but it is not installed on this PC.",
                "Download and install the runtime, then start Ruse again. Windows 11 normally includes it; some Windows 10, LTSC and Server installs do not.",
                "Download WebView2",
                "https://go.microsoft.com/fwlink/p/?LinkId=2124703")
            {
                Owner = this,
            };
            missingDialog.ShowDialog();
            Close();
            Application.Current.Shutdown();
            return;
        }

        try
        {
            await _controller.InitializeAsync(WebView);
            CreateTrayIcon();
            if (_controller.UserSettings.StartMinimized && _tray != null)
            {
                HideToTray();
            }
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
