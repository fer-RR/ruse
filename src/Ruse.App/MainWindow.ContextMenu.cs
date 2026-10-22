/// <summary>
/// ContextMenu part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Canvas context menu ----------

    private Point _contextMenuPoint;

    /// <summary>Remembers where the canvas was right-clicked.</summary>
    private void OnCanvasRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextMenuPoint = e.GetPosition(ConnectionCanvas);
    }

    /// <summary>
    /// Right-clicking the canvas offers the same things the sidebar offers:
    /// add a source, an output or an effect right where you clicked.
    /// </summary>
    private void OnCanvasContextMenuOpening(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        // Where the click happened, in canvas coordinates.
        Point at = _contextMenuPoint;
        var addSource = new MenuItem { Header = "Add source" };
        var addOutput = new MenuItem { Header = "Add output" };
        var addEffect = new MenuItem { Header = "Add effect" };

        if (_allApps.Count == 0 && _allInputs.Count == 0 && _allOutputs.Count == 0)
        {
            addSource.IsEnabled = false;
            addOutput.IsEnabled = false;
        }

        foreach (var app in _allApps)
        {
            var item = new MenuItem { Header = app.Label };
            item.Click += (_, _) => AddAppAt(app, at);
            addSource.Items.Add(item);
        }

        foreach (var device in _allInputs)
        {
            var item = new MenuItem { Header = device.FriendlyName };
            item.Click += (_, _) => AddInputAt(device, at);
            addSource.Items.Add(item);
        }

        foreach (var device in _allOutputs)
        {
            var item = new MenuItem { Header = device.FriendlyName };
            item.Click += (_, _) => AddOutputAt(device, at);
            addOutput.Items.Add(item);
        }

        foreach (var descriptor in AudioProcessorCatalog.All)
        {
            var item = new MenuItem { Header = descriptor.DisplayName };
            item.Click += (_, _) => AddEffectAt(descriptor, at);
            addEffect.Items.Add(item);
        }

        if (addEffect.Items.Count == 0)
        {
            addEffect.IsEnabled = false;
        }

        var systemAudio = new MenuItem { Header = "System audio (everything)" };
        systemAudio.Click += (_, _) => AddSystemAudioAt(at);

        var paste = new MenuItem { Header = "Paste", IsEnabled = false };
        var selectAll = new MenuItem { Header = "Select all" };
        var tidy = new MenuItem { Header = "Tidy up (arrange nodes)" };
        tidy.Click += (_, _) => TidyUp();

        menu.Items.Clear();
        menu.Items.Add(addSource);
        menu.Items.Add(addOutput);
        menu.Items.Add(addEffect);
        menu.Items.Add(systemAudio);
        menu.Items.Add(new Separator());
        menu.Items.Add(paste);
        menu.Items.Add(selectAll);
        menu.Items.Add(tidy);
    }

    private void AddAppAt(AudioSessionInfo app, Point at)
    {
        var source = new SourceNode(app.Label, SourceKind.Application, app.ProcessId);
        var vm = AddSourceNode(source, at.X, at.Y);
        if (vm != null)
        {
            SetStatus($"Added '{app.Label}'.");
        }
    }

    private void AddInputAt(DeviceItem device, Point at)
    {
        var source = new SourceNode(device.FriendlyName, SourceKind.Device, deviceId: device.Id);
        var vm = AddSourceNode(source, at.X, at.Y);
        if (vm != null)
        {
            SetStatus($"Added '{device.FriendlyName}'.");
        }
    }

    private void AddOutputAt(DeviceItem device, Point at)
    {
        var vm = AddOutputNode(device, at.X, at.Y);
        if (vm != null)
        {
            SetStatus($"Added '{device.FriendlyName}'.");
        }
    }

    private void AddEffectAt(AudioProcessorCatalog.Descriptor descriptor, Point at)
    {
        var modifier = new ModifierNode(descriptor.DisplayName);
        modifier.Add(descriptor.Create());
        var vm = AddModifierNodeCore(modifier, descriptor.DisplayName, at.X, at.Y);
        if (vm != null)
        {
            SetStatus($"Added '{descriptor.DisplayName}'.");
        }
    }

    private void AddSystemAudioAt(Point at)
    {
        var source = new SourceNode("System audio (everything)", SourceKind.SystemLoopback);
        var vm = AddSourceNode(source, at.X, at.Y);
        if (vm != null)
        {
            SetStatus("Added system audio.");
        }
    }

    /// <summary>Lines the nodes up in a simple left-to-right flow.</summary>
    private void TidyUp()
    {
        var sources = _nodes.Where(vm => vm.Kind == NodeKind.Source).ToList();
        var modifiers = _nodes.Where(vm => vm.Kind == NodeKind.Modifier).ToList();
        var outputs = _nodes.Where(vm => vm.Kind == NodeKind.Output).ToList();

        const double column = 300;
        const double row = 170;

        int rowIndex = 0;
        foreach (var vm in sources.Concat(modifiers))
        {
            vm.X = SnapToGrid(40 + (rowIndex % 3) * column);
            vm.Y = SnapToGrid(40 + (rowIndex / 3) * row);
            rowIndex++;
        }

        rowIndex = 0;
        foreach (var vm in outputs)
        {
            vm.X = SnapToGrid(40 + (rowIndex % 2) * column);
            vm.Y = SnapToGrid(40 + 220 + (rowIndex / 2) * row);
            rowIndex++;
        }

        UpdateGraphExtent();
        SetStatus("Nodes arranged.");
    }
}
