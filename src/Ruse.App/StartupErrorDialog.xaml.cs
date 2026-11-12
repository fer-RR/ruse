using System.Diagnostics;
using System.Windows;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>Shown when the WebView2 host cannot start at all.</summary>
public partial class StartupErrorDialog : Window
{
    private readonly string? _actionUrl;

    public StartupErrorDialog(string message, string detail, string? actionLabel = null, string? actionUrl = null)
    {
        InitializeComponent();
        MessageText.Text = message;
        DetailText.Text = detail;
        _actionUrl = actionUrl;
        if (!string.IsNullOrWhiteSpace(actionLabel) && !string.IsNullOrWhiteSpace(actionUrl))
        {
            ActionButton.Content = actionLabel;
            ActionButton.Visibility = Visibility.Visible;
        }
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_actionUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_actionUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("The WebView2 download page could not be opened.", ex);
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        AppLog.OpenLog();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
