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

    private void OnSidebarItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem == null)
        {
            return;
        }

        switch (list.SelectedItem)
        {
            case AudioSessionInfo info:
                AddSourceNode(new SourceNode(info.Label, SourceKind.Application, info.ProcessId));
                break;

            case DeviceItem device when ReferenceEquals(list, InputsList):
                AddDeviceSourceNode(device);
                break;

            case DeviceItem device:
                AddOutputNode(device);
                break;

            case VirtualOutputInfo info:
                AddVirtualOutputNode(info);
                break;

            case AudioProcessorCatalog.Descriptor descriptor:
                AddModifierNode(descriptor);
                break;

            case SubgraphDefinition definition:
                AddChainNode(definition, null);
                break;
        }
    }
}
