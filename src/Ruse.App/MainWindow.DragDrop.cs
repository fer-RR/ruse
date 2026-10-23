/// <summary>
/// DragDrop part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Drag from sidebar to canvas ----------

    private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _listDragSource = sender as ListBox;
        _listDragStart = e.GetPosition(this);
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _listDragSource == null)
        {
            return;
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _listDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _listDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = (_listDragSource.SelectedItem);
        if (item == null)
        {
            return;
        }

        object payload = _listDragSource switch
        {
            var lb when lb == OutputsList && item is DeviceItem od => new DropPayload(DropKind.OutputDevice, Device: od),
            var lb when lb == InputsList && item is DeviceItem id => new DropPayload(DropKind.CaptureDevice, Device: id),
            var lb when lb == AppsList && item is AudioSessionInfo ai => new DropPayload(DropKind.Application, App: ai),
            var lb when lb == VirtualList && item is VirtualOutputInfo vi => new DropPayload(DropKind.VirtualOutput, Virtual: vi),
            var lb when lb == ModifiersList && item is AudioProcessorCatalog.Descriptor md => new DropPayload(DropKind.Modifier, Modifier: md),
            var lb when lb == ChainsList && item is SubgraphDefinition cd => new DropPayload(DropKind.Chain, Chain: cd),
            _ => item,
        };

        var data = new DataObject(NodeDragFormat, payload);
        DragDrop.DoDragDrop(_listDragSource, data, DragDropEffects.Copy);
    }

    private void OnListPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _listDragSource = null;
        _listDragStart = default;
    }

    private void OnCanvasDragEffect(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(NodeDragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(NodeDragFormat))
        {
            return;
        }

        var payload = e.Data.GetData(NodeDragFormat);
        Point dropPos = e.GetPosition(NodeCanvas);

        // Offset so the node lands roughly centred under the cursor.
        double x = Math.Max(0, dropPos.X - 95);
        double y = Math.Max(0, dropPos.Y - 40);

        HandleDropPayload(payload, x, y);

        e.Handled = true;
    }

    /// <summary>
    /// Creates the node a drop payload asks for. Split out from the drag
    /// handler so the same path can be exercised by the self-test.
    /// </summary>
    internal void HandleDropPayload(object? payload, double x, double y)
    {
        switch (payload)
        {
            case DropPayload { Kind: DropKind.Application, App: { } info }:
                AddSourceNode(new SourceNode(info.Label, SourceKind.Application, info.ProcessId), x, y);
                break;
            case DropPayload { Kind: DropKind.VirtualOutput, Virtual: { } vInfo }:
                AddVirtualOutputNode(vInfo, x, y);
                break;
            case DropPayload { Kind: DropKind.OutputDevice, Device: { } od }:
                AddOutputNode(od, x, y);
                break;
            case DropPayload { Kind: DropKind.CaptureDevice, Device: { } id }:
                AddDeviceSourceNode(id, x, y);
                break;
            case DropPayload { Kind: DropKind.Modifier, Modifier: { } md }:
                AddModifierNode(md, x, y);
                break;
            case DropPayload { Kind: DropKind.Chain, Chain: { } chain }:
                AddChainNode(chain, new Point(x, y));
                break;
        }
    }

    private enum DropKind
    {
        Application,
        OutputDevice,
        CaptureDevice,
        VirtualOutput,
        Modifier,
        Chain,
    }

    private sealed record DropPayload(
        DropKind Kind,
        AudioSessionInfo? App = null,
        DeviceItem? Device = null,
        VirtualOutputInfo? Virtual = null,
        AudioProcessorCatalog.Descriptor? Modifier = null,
        SubgraphDefinition? Chain = null);
}
