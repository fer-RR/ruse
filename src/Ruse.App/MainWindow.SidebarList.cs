/// <summary>
/// SidebarList part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Sidebar interactions ----------

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshAllDevices();
        RefreshApps();
    }

    private void OnAppDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppsList.SelectedItem is AudioSessionInfo info)
        {
            AddSourceNode(new SourceNode(info.Label, SourceKind.Application, info.ProcessId));
        }
    }

    private void OnOutputDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OutputsList.SelectedItem is DeviceItem item)
        {
            AddOutputNode(item);
        }
    }

    private void OnInputDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (InputsList.SelectedItem is DeviceItem item)
        {
            AddDeviceSourceNode(item);
        }
    }

    private void OnVirtualDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VirtualList.SelectedItem is VirtualOutputInfo info)
        {
            AddVirtualOutputNode(info);
        }
    }

    private void OnModifierDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModifiersList.SelectedItem is AudioProcessorCatalog.Descriptor md)
        {
            AddModifierNode(md);
        }
    }
}
