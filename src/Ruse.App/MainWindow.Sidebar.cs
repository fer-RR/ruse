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
    /// Keeps the sidebar scrolling when the wheel is over a list that has
    /// already reached its end - a WPF ListBox swallows the wheel even when
    /// it cannot scroll any further, which used to stop the sidebar dead.
    /// </summary>
    private void OnSidebarListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScrollViewer(sender as DependencyObject) is not ScrollViewer inner)
        {
            return;
        }

        bool atTop = inner.VerticalOffset <= 0.01;
        bool atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.01;

        if ((e.Delta < 0 && atTop) || (e.Delta > 0 && atBottom))
        {
            // Let the event bubble so the surrounding ScrollViewer takes over.
            e.Handled = false;
        }
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
        var result = SettingsDialog.Show(this, _settings.HighlightMutedNodes, _settings.MinimapMode);
        if (result == null)
        {
            return;
        }

        _settings.HighlightMutedNodes = result.HighlightMutedNodes;
        _settings.MinimapMode = result.MinimapMode;
        _settings.Save();

        // Apply the appearance options right away.
        RefreshNodeAppearance();
        UpdateMinimapVisibility();

        SetStatus("Settings saved.");
    }
}
