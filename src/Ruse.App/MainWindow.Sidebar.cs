/// <summary>
/// Sidebar part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Sidebar ----------
    /// <summary>
    /// Collapses or expands a sidebar section. The toggle carries the name of
    /// the panel it controls.
    /// </summary>
    private void OnSectionToggle(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle && toggle.Tag is string panelName &&
            FindName(panelName) is UIElement panel)
        {
            panel.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The sidebar lists live inside the sidebar's own ScrollViewer, but a WPF
    /// ListBox still swallows the wheel when the pointer is over one of its
    /// items - even when the list itself has nothing to scroll. When the list
    /// cannot move in the wheel's direction, scroll the sidebar directly and
    /// mark the event handled so the list cannot stop it.
    /// </summary>
    private void OnSidebarListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var inner = FindScrollViewer(sender as DependencyObject);

        // If the list itself can scroll this way, leave the event alone so it
        // scrolls normally instead of moving the whole sidebar.
        if (inner != null)
        {
            bool canScrollDown = e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.01;
            bool canScrollUp = e.Delta > 0 && inner.VerticalOffset > 0.01;
            if (canScrollDown || canScrollUp)
            {
                return;
            }
        }

        // Three 16 px lines per wheel notch, the usual Windows step.
        double step = e.Delta / 120.0 * 48.0;
        SidebarScroll.ScrollToVerticalOffset(SidebarScroll.VerticalOffset - step);
        e.Handled = true;
    }

    /// <summary>Finds the ScrollViewer inside a control.</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root == null)
        {
            return null;
        }

        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private void OnShortcutsClick(object sender, RoutedEventArgs e) => ShortcutDialog.Show(this);

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var result = SettingsDialog.Show(
            this,
            _settings.HighlightMutedNodes,
            _settings.MinimapMode,
            StartupRegistration.IsEnabled(),
            _settings.StartRoutingOnLaunch);
        if (result == null)
        {
            return;
        }

        string? startupError = null;
        try
        {
            StartupRegistration.SetEnabled(result.StartWithWindows);
        }
        catch (Exception ex)
        {
            startupError = ex.Message;
            AppLog.Error("Could not update the Windows startup entry.", ex);
        }

        _settings.HighlightMutedNodes = result.HighlightMutedNodes;
        _settings.MinimapMode = result.MinimapMode;
        _settings.StartRoutingOnLaunch = result.StartRoutingOnLaunch;
        _settings.Save();

        // Apply the appearance options right away.
        RefreshNodeAppearance();
        UpdateMinimapVisibility();

        if (startupError != null)
        {
            ShowAlert("Settings saved, but Windows startup could not be changed: " + startupError,
                isError: true);
            SetStatus("Settings saved; the startup change failed.");
        }
        else
        {
            string status = "Settings saved.";
            if (result.StartRoutingOnLaunch)
            {
                status += " Routing will start on the next launch.";
            }

            if (result.StartWithWindows)
            {
                status += " Ruse will start with Windows.";
            }

            SetStatus(status);
        }
    }
}
