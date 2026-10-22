/// <summary>
/// History part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Undo / redo ----------

    private readonly UndoHistory _history = new();

    /// <summary>Set while an undo/redo is being applied, so it does not record itself.</summary>
    private bool _historySuspended;

    /// <summary>Everything needed to put a node back exactly as it was.</summary>
    private sealed record NodeData(
        string Id,
        NodeKind Kind,
        string Title,
        string? DeviceId,
        uint? ProcessId,
        SourceKind SourceKind,
        bool IsVirtual,
        string? MicHint,
        double X,
        double Y,
        float Volume,
        bool Muted,
        List<PresetProcessor> Processors);

    /// <summary>
    /// Finds a node by its stable id. Undo commands keep ids rather than node
    /// references, because undoing an "add node" recreates the node object and
    /// any reference to the old one would go stale.
    /// </summary>
    private NodeViewModel? FindNodeById(string id)
    {
        foreach (var vm in _nodeBacking.Keys)
        {
            if (string.Equals(vm.Id, id, StringComparison.Ordinal))
            {
                return vm;
            }
        }

        return null;
    }

    private NodeData CaptureNodeData(NodeViewModel vm)
    {
        var data = new NodeData(
            vm.Id, vm.Kind, vm.Title, null, null, SourceKind.Application, false, vm.MicHint,
            vm.X, vm.Y, vm.Volume, vm.Muted, new List<PresetProcessor>());

        if (!_nodeBacking.TryGetValue(vm, out var obj))
        {
            return data;
        }

        switch (obj)
        {
            case SourceNode source:
                data = data with { DeviceId = source.DeviceId, ProcessId = source.ProcessId, SourceKind = source.Kind };
                break;
            case OutputNode output:
                data = data with { DeviceId = output.DeviceId, IsVirtual = vm.MicHint != null };
                break;
            case ModifierNode modifier:
                data = data with
                {
                    Processors = modifier.Processors.Select(PresetSerializer.SerializeProcessor).ToList(),
                };
                break;
        }

        return data;
    }

    /// <summary>Recreates a node from captured data and puts it back on the canvas.</summary>
    private NodeViewModel? RestoreNode(NodeData data)
    {
        NodeViewModel? vm = data.Kind switch
        {
            NodeKind.Source => AddSourceNode(
                new SourceNode(data.Title, data.SourceKind, data.ProcessId, data.DeviceId), data.X, data.Y, data.Id),
            NodeKind.Output => RestoreOutputNode(data),
            NodeKind.Modifier => RestoreModifierNode(data),
            _ => null,
        };

        if (vm != null)
        {
            vm.Volume = data.Volume;
            vm.Muted = data.Muted;
        }

        return vm;
    }

    private NodeViewModel? RestoreOutputNode(NodeData data)
    {
        if (string.IsNullOrEmpty(data.DeviceId))
        {
            return null;
        }

        try
        {
            if (data.IsVirtual)
            {
                return AddVirtualOutputNode(
                    VirtualDeviceCatalog.ResolveRender(data.DeviceId, data.Title, data.MicHint), data.X, data.Y, data.Id);
            }

            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(data.DeviceId);
            return AddOutputNode(new DeviceItem(device), data.X, data.Y, data.Id);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Could not restore the output '{data.Title}': {ex.Message}");
            return null;
        }
    }

    private NodeViewModel? RestoreModifierNode(NodeData data)
    {
        var modifier = new ModifierNode(data.Title);
        foreach (var entry in data.Processors)
        {
            var processor = PresetSerializer.CreateProcessor(entry);
            if (processor != null)
            {
                modifier.Add(processor);
            }
        }

        return AddModifierNodeCore(modifier, data.Title, data.X, data.Y, data.Id);
    }

    /// <summary>The links a node takes part in, as live node references.</summary>
    private List<(NodeViewModel From, NodeViewModel To)> CaptureLinks(NodeViewModel vm)
    {
        var links = new List<(NodeViewModel, NodeViewModel)>();
        foreach (var conn in _connections)
        {
            if (ReferenceEquals(conn.From.Owner, vm) || ReferenceEquals(conn.To.Owner, vm))
            {
                links.Add((conn.From.Owner, conn.To.Owner));
            }
        }

        return links;
    }

    // ---------- History helpers ----------

    private void PushAddNode(NodeViewModel vm)
    {
        if (_historySuspended)
        {
            return;
        }

        NodeData data = CaptureNodeData(vm);
        NodeViewModel current = vm;
        _history.Push(new DelegateUndoCommand(
            "Add node",
            undo: () => RemoveNodeInternal(current),
            redo: () => { current = RestoreNode(data) ?? current; }));
    }

    private void PushRemoveNode(NodeViewModel vm)
    {
        if (_historySuspended)
        {
            return;
        }

        NodeData data = CaptureNodeData(vm);
        var links = CaptureLinks(vm);
        NodeViewModel current = vm;

        _history.Push(new DelegateUndoCommand(
            "Remove node",
            undo: () =>
            {
                NodeViewModel? restored = RestoreNode(data);
                if (restored == null)
                {
                    return;
                }

                current = restored;
                foreach (var (from, to) in links)
                {
                    ConnectNodes(from, to);
                }
            },
            redo: () => RemoveNodeInternal(current)));
    }

    private void PushMoveNode(NodeViewModel vm, double oldX, double oldY, double newX, double newY)
    {
        if (_historySuspended || (Math.Abs(oldX - newX) < 0.5 && Math.Abs(oldY - newY) < 0.5))
        {
            return;
        }

        string id = vm.Id;
        _history.Push(new DelegateUndoCommand(
            "Move node",
            undo: () =>
            {
                var node = FindNodeById(id);
                if (node != null)
                {
                    node.X = oldX;
                    node.Y = oldY;
                }
            },
            redo: () =>
            {
                var node = FindNodeById(id);
                if (node != null)
                {
                    node.X = newX;
                    node.Y = newY;
                }
            }));
    }

    private void PushConnect(NodeViewModel from, NodeViewModel to)
    {
        if (_historySuspended)
        {
            return;
        }

        string fromId = from.Id;
        string toId = to.Id;
        _history.Push(new DelegateUndoCommand(
            "Connect",
            undo: () =>
            {
                var f = FindNodeById(fromId);
                var t = FindNodeById(toId);
                if (f != null && t != null)
                {
                    DisconnectNodes(f, t);
                }
            },
            redo: () =>
            {
                var f = FindNodeById(fromId);
                var t = FindNodeById(toId);
                if (f != null && t != null)
                {
                    ConnectNodes(f, t);
                }
            }));
    }

    private void PushDisconnect(NodeViewModel from, NodeViewModel to)
    {
        if (_historySuspended)
        {
            return;
        }

        string fromId = from.Id;
        string toId = to.Id;
        _history.Push(new DelegateUndoCommand(
            "Disconnect",
            undo: () =>
            {
                var f = FindNodeById(fromId);
                var t = FindNodeById(toId);
                if (f != null && t != null)
                {
                    ConnectNodes(f, t);
                }
            },
            redo: () =>
            {
                var f = FindNodeById(fromId);
                var t = FindNodeById(toId);
                if (f != null && t != null)
                {
                    DisconnectNodes(f, t);
                }
            }));
    }

    private void PushValue(string label, string key, Action<float> apply, float oldValue, float newValue)
    {
        if (_historySuspended || Math.Abs(oldValue - newValue) < 0.0001f)
        {
            return;
        }

        _history.Push(new ValueUndoCommand(label, key, apply, oldValue, newValue));
    }

    /// <summary>Records a node's volume, resolving the node by id when applied.</summary>
    private void PushVolume(NodeViewModel vm, float oldValue, float newValue)
    {
        string id = vm.Id;
        PushValue("Volume", "volume:" + id, v =>
        {
            var node = FindNodeById(id);
            if (node != null)
            {
                node.Volume = v;
            }
        }, oldValue, newValue);
    }

    /// <summary>Records a node's mute state, resolving the node by id when applied.</summary>
    private void PushMute(NodeViewModel vm, bool oldValue, bool newValue)
    {
        string id = vm.Id;
        PushValue("Mute", "mute:" + id, v =>
        {
            var node = FindNodeById(id);
            if (node != null)
            {
                node.Muted = v >= 0.5f;
            }
        }, oldValue ? 1 : 0, newValue ? 1 : 0);
    }

    /// <summary>
    /// Records an effect parameter change. The parameter is looked up again on
    /// the live modifier when the change is undone, so it survives a node being
    /// removed and restored.
    /// </summary>
    private void PushParameter(NodeViewModel vm, string parameterName, float oldValue, float newValue)
    {
        string id = vm.Id;
        PushValue(
            $"{vm.Title}: {parameterName}",
            $"param:{id}:{parameterName}",
            v =>
            {
                var node = FindNodeById(id);
                if (node == null || !_nodeBacking.TryGetValue(node, out var obj) || obj is not ModifierNode modifier)
                {
                    return;
                }

                foreach (var processor in modifier.Processors.OfType<IParameterizedProcessor>())
                {
                    var parameter = processor.Parameters.FirstOrDefault(p => p.Name == parameterName);
                    parameter?.Set(v);
                }
            },
            oldValue,
            newValue);
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (!_history.CanUndo)
        {
            return;
        }

        _historySuspended = true;
        try
        {
            _history.Undo();
        }
        finally
        {
            _historySuspended = false;
        }

        SetStatus("Undone.");
    }

    private void Redo()
    {
        if (!_history.CanRedo)
        {
            return;
        }

        _historySuspended = true;
        try
        {
            _history.Redo();
        }
        finally
        {
            _historySuspended = false;
        }

        SetStatus("Redone.");
    }

    private void UpdateHistoryButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        UndoButton.ToolTip = _history.CanUndo ? $"Undo: {_history.UndoLabel} (Ctrl+Z)" : "Nothing to undo";
        RedoButton.ToolTip = _history.CanRedo ? $"Redo: {_history.RedoLabel} (Ctrl+Y)" : "Nothing to redo";
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // A text field gets the keyboard to itself - except F1, which should
        // still open the shortcut list.
        if (Keyboard.FocusedElement is TextBox && e.Key != Key.F1)
        {
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z:
                    e.Handled = true;
                    if (shift)
                    {
                        Redo();
                    }
                    else
                    {
                        Undo();
                    }

                    return;
                case Key.Y:
                    e.Handled = true;
                    Redo();
                    return;
                case Key.S:
                    e.Handled = true;
                    OnSavePresetClick(this, new RoutedEventArgs());
                    return;
                case Key.O:
                    e.Handled = true;
                    OnLoadPresetClick(this, new RoutedEventArgs());
                    return;
                case Key.F:
                    e.Handled = true;
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                    return;
                case Key.D:
                    e.Handled = true;
                    DuplicateSelected();
                    return;
                case Key.D1:
                case Key.D2:
                case Key.D3:
                case Key.D4:
                case Key.D5:
                    e.Handled = true;
                    ToggleSidebarSection(e.Key - Key.D1);
                    return;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (_dragLine != null)
                {
                    // Cancel a cable that is being dragged.
                    NodeCanvas.ReleaseMouseCapture();
                    ConnectionCanvas.Children.Remove(_dragLine);
                    _dragLine = null;
                    _dragFrom = null;
                    SetDropHighlight(null);
                    SetStatus("Connection cancelled.");
                    e.Handled = true;
                }

                return;
            case Key.Delete:
            case Key.Back:
                if (_selected != null)
                {
                    e.Handled = true;
                    RemoveNode(_selected);
                }

                return;
            case Key.M:
                if (_selected != null)
                {
                    e.Handled = true;
                    _selected.Muted = !_selected.Muted;
                    SetStatus($"'{_selected.Title}' {( _selected.Muted ? "muted" : "unmuted")}.");
                }

                return;
            case Key.F:
                e.Handled = true;
                FitGraphToView();
                return;
            case Key.D0:
            case Key.NumPad0:
                e.Handled = true;
                ResetZoom();
                return;
            case Key.OemPlus:
            case Key.Add:
            case Key.OemMinus:
            case Key.Subtract:
                e.Handled = true;
                ZoomAt(ZoomAnchorPoint(), e.Key == Key.OemPlus || e.Key == Key.Add ? 1.15 : 1 / 1.15);
                return;
            case Key.F1:
                e.Handled = true;
                ShortcutDialog.Show(this);
                return;
        }
    }

    /// <summary>The point in the viewport the zoom should keep in place.</summary>
    private Point ZoomAnchorPoint()
    {
        double w = GraphScroll.ActualWidth > 0 ? GraphScroll.ActualWidth : 800;
        double h = GraphScroll.ActualHeight > 0 ? GraphScroll.ActualHeight : 600;
        return new Point(w / 2, h / 2);
    }

    /// <summary>Collapses or expands one of the sidebar sections.</summary>
    private void ToggleSidebarSection(int index)
    {
        ToggleButton[] toggles = { AppsToggle, OutputsToggle, InputsToggle, VirtualToggle, ModifiersToggle };
        if (index < 0 || index >= toggles.Length)
        {
            return;
        }

        ToggleButton toggle = toggles[index];
        toggle.IsChecked = toggle.IsChecked != true;
        OnSectionToggle(toggle, new RoutedEventArgs());
    }

    /// <summary>Copies the selected node, a little to the right of the original.</summary>
    private void DuplicateSelected()
    {
        if (_selected == null)
        {
            return;
        }

        var data = CaptureNodeData(_selected);
        NodeViewModel? copy = RestoreNode(data with { X = data.X + 40, Y = data.Y + 40 });
        if (copy != null)
        {
            _selected = copy;
            SetStatus($"Duplicated '{data.Title}'.");
        }
    }

    /// <summary>Zooms and scrolls so every node fits in the view.</summary>
    private void FitGraphToView()
    {
        if (_nodes.Count == 0)
        {
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var vm in _nodes)
        {
            double nodeHeight = vm.Height;
            if (_nodeVisuals.TryGetValue(vm, out var body) && body.ActualHeight > 0)
            {
                nodeHeight = body.ActualHeight;
            }

            minX = Math.Min(minX, vm.X);
            minY = Math.Min(minY, vm.Y);
            maxX = Math.Max(maxX, vm.X + vm.Width);
            maxY = Math.Max(maxY, vm.Y + nodeHeight);
        }

        double viewportW = GraphScroll.ViewportWidth > 0 ? GraphScroll.ViewportWidth : GraphScroll.ActualWidth;
        double viewportH = GraphScroll.ViewportHeight > 0 ? GraphScroll.ViewportHeight : GraphScroll.ActualHeight;
        if (viewportW <= 0 || viewportH <= 0)
        {
            return;
        }

        const double margin = 60;
        double worldW = Math.Max(maxX - minX, 1);
        double worldH = Math.Max(maxY - minY, 1);
        double scale = Math.Clamp(
            Math.Min((viewportW - margin) / worldW, (viewportH - margin) / worldH),
            MinZoom,
            MaxZoom);

        _zoomTransform.ScaleX = scale;
        _zoomTransform.ScaleY = scale;
        UpdateGraphExtent();
        UpdateLayout();

        // Centre the content.
        GraphScroll.ScrollToHorizontalOffset(((minX + (worldW / 2)) * scale) - (viewportW / 2));
        GraphScroll.ScrollToVerticalOffset(((minY + (worldH / 2)) * scale) - (viewportH / 2));

        UpdateCableThickness();
        UpdateZoomIndicator();
        SetStatus("Graph fitted to the view.");
    }
}
