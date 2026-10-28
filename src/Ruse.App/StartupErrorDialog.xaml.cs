using System.Windows;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>Shown when the WebView2 host cannot start at all.</summary>
public partial class StartupErrorDialog : Window
{
    public StartupErrorDialog(string message, string detail)
    {
        InitializeComponent();
        MessageText.Text = message;
        DetailText.Text = detail;
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
