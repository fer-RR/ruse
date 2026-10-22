/// <summary>
/// Diagnostics part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Problem reporting ----------

    /// <summary>Shows a message in the critical-message bar and logs it.</summary>
    private void ShowAlert(string message, bool isError)
    {
        AlertText.Text = message;
        AlertIcon.Text = isError ? "⚠" : "ⓘ";
        AlertIcon.Foreground = isError
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF8A8A"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8AB4FF"));
        AlertBar.Visibility = Visibility.Visible;
    }

    private void OnAlertDismissClick(object sender, RoutedEventArgs e) => AlertBar.Visibility = Visibility.Collapsed;

    private void OnAlertLogClick(object sender, RoutedEventArgs e) => AppLog.OpenLog();

    private void OnErrorButtonClick(object sender, RoutedEventArgs e) => AppLog.OpenLog();

    private void UpdateErrorIndicator()
    {
        int count = AppLog.ErrorCount;
        ErrorButton.Content = $"⚠ {count}";
        ErrorButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Self-test for the undo/redo stack: connecting, adding a node and
    /// changing a value must all be reversible, in both directions.
    /// </summary>
    public string RunUndoSelfTest()
    {
        int linksBefore = _connections.Count;
        int nodesBefore = _nodes.Count;

        var output = AddTestSinkOutput(950, 520);
        var source = AddSourceNode(new SourceNode("Undo source", SourceKind.Application, 0), 40, 520);
        if (output == null || source == null)
        {
            return "undo: setup failed -> FAIL";
        }

        // Add-source is already on the history; connect on top of it.
        bool connected = TryConnect(source, output);
        int linksAfterConnect = _connections.Count;

        Undo();
        int linksAfterUndo = _connections.Count;

        Redo();
        int linksAfterRedo = _connections.Count;

        Undo();
        Undo();
        int nodesAfterUndos = _nodes.Count;
        string depth = $"depth={(_history.CanUndo ? "u" : "-")}{(_history.CanRedo ? "r" : "-")}";

        Redo();
        Redo();
        int nodesAfterRedos = _nodes.Count;
        int linksAfterRedos = _connections.Count;

        // A value change must be reversible as well.
        float volumeBefore = source.Volume;
        source.Volume = 0.42f;
        PushValue("Volume", "volume:test", v => source.Volume = v, volumeBefore, 0.42f);
        Undo();
        float volumeAfterUndo = source.Volume;
        Redo();
        float volumeAfterRedo = source.Volume;

        // So must a rename. (The node was removed and restored by the undos
        // above, so the live one has to be looked up by id.)
        var liveSource = FindNodeById(source.Id) ?? source;
        string titleBefore = liveSource.Title;
        ApplyNodeTitle(liveSource, "Renamed source");
        PushRename(liveSource, titleBefore, "Renamed source");
        Undo();
        string titleAfterUndo = liveSource.Title;
        Redo();
        string titleAfterRedo = liveSource.Title;

        // A saved chain must land on the canvas as a single node.
        var chainDefinition = new SubgraphDefinition { Name = "Perf chain" };
        chainDefinition.Elements.Add(new SubgraphElement
        {
            TypeId = GainProcessor.Id,
            Parameters = { ["Gain"] = 0.5f },
        });

        NodeViewModel? chainVm = AddChainNode(chainDefinition, new Point(100, 700));
        bool chainOk = chainVm != null && chainVm.Kind == NodeKind.Modifier;

        // The same chain, dropped from the sidebar list, must land too.
        int beforeDrop = _nodes.Count;
        HandleDropPayload(new DropPayload(DropKind.Chain, Chain: chainDefinition), 320, 860);
        bool chainDropOk = _nodes.Count == beforeDrop + 1 &&
                           _nodes[^1].Title == chainDefinition.Name;

        bool pass =
            connected &&
            linksAfterConnect == linksBefore + 1 &&
            linksAfterUndo == linksBefore &&
            linksAfterRedo == linksAfterConnect &&
            nodesAfterUndos == nodesBefore + 1 &&
            nodesAfterRedos == nodesBefore + 2 &&
            linksAfterRedos == linksBefore + 1 &&
            Math.Abs(volumeAfterUndo - volumeBefore) < 0.001f &&
            Math.Abs(volumeAfterRedo - 0.42f) < 0.001f &&
            titleAfterUndo == titleBefore &&
            titleAfterRedo == "Renamed source" &&
            chainOk &&
            chainDropOk;

        return $"undo: links {linksBefore}->{linksAfterConnect}->{linksAfterUndo}->{linksAfterRedo}, " +
               $"nodes {nodesBefore}->{nodesAfterUndos}->{nodesAfterRedos} ({depth}), " +
               $"links after redo {linksAfterRedos}, " +
               $"volume {volumeBefore:0.00}->{volumeAfterUndo:0.00}->{volumeAfterRedo:0.00}, " +
               $"title '{titleBefore}'->'{titleAfterUndo}'->'{titleAfterRedo}' " +
               $"-> {(pass ? "PASS" : "FAIL")}";
    }

    /// <summary>
    /// Self-test for the appearance options: a muted node must get a red
    /// border while the option is on, and a normal border while it is off.
    /// </summary>
    public string RunAppearanceSelfTest()
    {
        var vm = AddModifierNodeCore(new ModifierNode("Appearance test"), "Appearance test", 420, 420);
        if (vm == null || !_nodeVisuals.TryGetValue(vm, out var body))
        {
            return "appearance: could not create the test node -> FAIL";
        }

        vm.Muted = true;

        _settings.HighlightMutedNodes = true;
        UpdateNodeAppearance(vm);
        bool redWhenOn = ReferenceEquals(body.BorderBrush, NodeMutedBorder);

        _settings.HighlightMutedNodes = false;
        UpdateNodeAppearance(vm);
        bool plainWhenOff = ReferenceEquals(body.BorderBrush, NodeIdleBorder);

        _settings.HighlightMutedNodes = true;
        UpdateNodeAppearance(vm);

        bool pass = redWhenOn && plainWhenOff;
        return $"appearance: red border when on={redWhenOn}, plain when off={plainWhenOff} " +
               $"-> {(pass ? "PASS" : "FAIL")}";
    }

    /// <summary>
    /// Self-test for the problem-reporting path: an error must reach the log,
    /// light up the critical-message bar and the status-bar counter, and the
    /// bar must be dismissable.
    /// </summary>
    public string RunAlertSelfTest()
    {
        int before = AppLog.ErrorCount;

        AppLog.Error("Self-test: synthetic problem.");
        ShowAlert("Self-test: synthetic problem.", isError: true);
        UpdateErrorIndicator();

        bool shown = AlertBar.Visibility == Visibility.Visible;
        bool counted = AppLog.ErrorCount == before + 1;
        bool badgeVisible = ErrorButton.Visibility == Visibility.Visible;

        OnAlertDismissClick(this, new RoutedEventArgs());
        bool dismissed = AlertBar.Visibility == Visibility.Collapsed;

        bool pass = shown && counted && badgeVisible && dismissed;
        return $"alert path: shown={shown} counted={counted} badge={badgeVisible} dismissed={dismissed} " +
               $"-> {(pass ? "PASS" : "FAIL")}";
    }

    /// <summary>
    /// Reports the graphics situation at startup. Windows renders WPF in software
    /// on some machines, which makes the whole UI feel slow — worth saying out
    /// loud instead of letting the user blame the app.
    /// </summary>
    private void CheckRenderingTier()
    {
        int tier = System.Windows.Media.RenderCapability.Tier;
        if (tier == 0x80000)
        {
            AppLog.Info("Windows is rendering Ruse with hardware acceleration (tier 2).");
            return;
        }

        string detail = tier == 0x20000
            ? "Windows is rendering Ruse in software (no GPU acceleration)."
            : "Windows is rendering Ruse with partial GPU acceleration.";

        AppLog.Warning(detail + " The interface will feel slower than usual. This is a system graphics " +
            "issue, not a problem with Ruse. A clean driver reinstall does not always fix it: on " +
            "Windows 11 24H2/25H2 the graphics stack can keep WPF in software mode. Things that do " +
            "help: turning off hardware-accelerated GPU scheduling, or installing an older GPU driver.");

        ShowAlert(detail + " The interface will feel slower than usual. This is a system graphics issue — " +
                  "a known Windows 11 24H2/25H2 quirk. Turning off hardware-accelerated GPU scheduling " +
                  "or installing an older GPU driver usually restores it. Details in the log.", isError: false);
    }

    private float UpstreamLevel(NodeViewModel node, int depth)
    {
        if (depth > 8)
        {
            return 0f;
        }

        foreach (var c in _connections)
        {
            if (!ReferenceEquals(c.To.Owner, node))
            {
                continue;
            }

            if (_nodeBacking.TryGetValue(c.From.Owner, out var obj) && obj is SourceNode source)
            {
                return source.MeterLevel;
            }

            float deeper = UpstreamLevel(c.From.Owner, depth + 1);
            if (deeper > 0f)
            {
                return deeper;
            }
        }

        return 0f;
    }

    /// <summary>Removes a single link between two nodes (any supported pairing).</summary>
    private void RemoveConnection(ConnectionViewModel conn)
    {
        var from = conn.From.Owner;
        var to = conn.To.Owner;

        DisconnectNodes(from, to);
        PushDisconnect(from, to);
    }

    /// <summary>
    /// Removes the link between two nodes (audio graph and cable). Does not
    /// touch the undo history.
    /// </summary>
    private void DisconnectNodes(NodeViewModel from, NodeViewModel to)
    {
        if (_nodeBacking.TryGetValue(from, out var fromObj) &&
            _nodeBacking.TryGetValue(to, out var toObj))
        {
            switch (fromObj, toObj)
            {
                case (SourceNode source, OutputNode output):
                    _graph.Disconnect(source, output);
                    break;
                case (SourceNode source, ModifierNode modifier):
                    _graph.DisconnectSourceFromModifier(source, modifier);
                    break;
                case (ModifierNode modifier, OutputNode output):
                    _graph.DisconnectModifierFromOutput(modifier, output);
                    break;
                case (ModifierNode first, ModifierNode second):
                    _graph.DisconnectModifierFromModifier(first, second);
                    break;
            }
        }

        var conn = _connections.FirstOrDefault(c =>
            ReferenceEquals(c.From.Owner, from) && ReferenceEquals(c.To.Owner, to));

        if (conn == null)
        {
            return;
        }

        if (conn.Cable != null)
        {
            ConnectionCanvas.Children.Remove(conn.Cable);
        }

        if (conn.HitPath != null)
        {
            ConnectionCanvas.Children.Remove(conn.HitPath);
        }

        _connections.Remove(conn);
    }

    /// <summary>Renames a node (display name + backing audio node title).</summary>
    private void RenameNode(NodeViewModel vm)
    {
        string? name = Ui.InputDialog.Show(this, "Rename node", "Node name", vm.Title);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = name.Trim();
        string oldTitle = vm.Title;
        if (oldTitle == name)
        {
            return;
        }

        ApplyNodeTitle(vm, name);
        PushRename(vm, oldTitle, name);
        SetStatus($"Renamed node to '{name}'.");
    }

    /// <summary>Applies a name to the node and its backing audio node.</summary>
    private void ApplyNodeTitle(NodeViewModel vm, string name)
    {
        vm.Title = name;

        if (!_nodeBacking.TryGetValue(vm, out var obj))
        {
            return;
        }

        switch (obj)
        {
            case SourceNode source:
                source.Title = name;
                break;
            case OutputNode output:
                output.Title = name;
                break;
            case ModifierNode modifier:
                modifier.Title = name;
                break;
        }
    }

    /// <summary>Records a rename so it can be undone and redone.</summary>
    private void PushRename(NodeViewModel vm, string oldTitle, string newTitle)
    {
        if (_historySuspended)
        {
            return;
        }

        string id = vm.Id;
        _history.Push(new DelegateUndoCommand(
            "Rename node",
            undo: () =>
            {
                var node = FindNodeById(id);
                if (node != null)
                {
                    ApplyNodeTitle(node, oldTitle);
                }
            },
            redo: () =>
            {
                var node = FindNodeById(id);
                if (node != null)
                {
                    ApplyNodeTitle(node, newTitle);
                }
            }));
    }

    /// <summary>Opens the gain prompt for a cable and applies the result to its route.</summary>
    private void PromptRouteGain(ConnectionViewModel conn)
    {
        float? gain = Ui.GainDialog.Show(this, $"Gain: {conn.From.Owner.Title} → {conn.To.Owner.Title}", conn.Gain);
        if (gain == null)
        {
            return;
        }

        ApplyRouteGain(conn, gain.Value);
        SetStatus($"Route gain set to {(int)Math.Round(gain.Value * 100)}%.");
    }

    private void ApplyRouteGain(ConnectionViewModel conn, float gain)
    {
        conn.Gain = gain;

        if (_nodeBacking.TryGetValue(conn.From.Owner, out var fromObj) &&
            _nodeBacking.TryGetValue(conn.To.Owner, out var toObj) &&
            fromObj is SourceNode source && toObj is OutputNode output)
        {
            var route = output.Inputs.FirstOrDefault(r => ReferenceEquals(r.Source, source));
            if (route != null)
            {
                route.Gain = gain;
                output.RefreshRoutes();
            }
        }

        // Thicker/dimmer cable hints at a boosted/cut route.
        if (conn.Cable != null)
        {
            conn.Cable.StrokeThickness = 1.5 + (gain * 2.5);
            conn.Cable.Opacity = 0.5 + (Math.Min(gain, 1f) * 0.4);
        }
    }

    /// <summary>Removes every route attached to the given node.</summary>
    private void DisconnectNode(NodeViewModel vm)
    {
        var affected = _connections
            .Where(c => c.From.Owner == vm || c.To.Owner == vm)
            .ToList();

        foreach (var conn in affected)
        {
            RemoveConnection(conn);
        }

        SetStatus(affected.Count > 0
            ? $"Disconnected {affected.Count} route(s) from '{vm.Title}'."
            : $"'{vm.Title}' had no connections.");
    }

    /// <summary>Removes a node and every route attached to it (undoable).</summary>
    private void RemoveNode(NodeViewModel vm)
    {
        PushRemoveNode(vm);

        // The routes that go with the node are part of the same undo step.
        _historySuspended = true;
        try
        {
            RemoveNodeInternal(vm);
        }
        finally
        {
            _historySuspended = false;
        }
    }

    /// <summary>Removes a node without recording an undo step.</summary>
    private void RemoveNodeInternal(NodeViewModel vm)
    {
        DisconnectNode(vm);

        if (_nodeHosts.TryGetValue(vm, out var host))
        {
            NodeCanvas.Children.Remove(host);
            _nodeHosts.Remove(vm);
        }

        _nodeVisuals.Remove(vm);

        if (_nodeBacking.TryGetValue(vm, out var backing))
        {
            switch (backing)
            {
                case SourceNode source:
                    _graph.RemoveSource(source);
                    break;
                case OutputNode output:
                    _graph.RemoveOutput(output);
                    break;
                case ModifierNode modifier:
                    _graph.RemoveModifier(modifier);
                    break;
            }

            _nodeBacking.Remove(vm);
        }

        _sourcePorts.Remove(vm);
        _outputPorts.Remove(vm);
        _nodes.Remove(vm);
        UpdateEmptyHint();
        UpdateGraphExtent();
        SetStatus($"Removed node '{vm.Title}'.");
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = _nodeBacking.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Self-test for the connection affordance: a cable dropped anywhere on a
    /// node's body must find that node, including on nodes that are much
    /// taller than their nominal height (a modifier with many parameters).
    /// </summary>
    public string RunDropTargetSelfTest()
    {
        var modifier = new ModifierNode("Tall test");
        modifier.Add(new GainProcessor { Gain = 0.8f });
        modifier.Add(new HighPassProcessor { Cutoff = 120 });

        var vm = AddModifierNodeCore(modifier, "Tall test", 120, 120);
        if (vm == null)
        {
            return "could not create the test node -> FAIL";
        }

        UpdateLayout();

        if (!_nodeVisuals.TryGetValue(vm, out var body))
        {
            return "test node has no visual -> FAIL";
        }

        double realHeight = body.ActualHeight;
        double nominal = vm.Height;
        Point origin = body.TranslatePoint(new Point(0, 0), ConnectionCanvas);

        // Middle of the body, and a point near its bottom edge.
        Point middle = new Point(origin.X + (body.ActualWidth / 2), origin.Y + (body.ActualHeight / 2));
        Point low = new Point(origin.X + 20, origin.Y + realHeight - 12);

        var hitMiddle = FindDropTargetAt(middle);
        var hitLow = FindDropTargetAt(low);
        var hitOrigin = FindDropTargetAt(new Point(origin.X + 5, origin.Y + 5));

        // A point far below the node must not match anything.
        var miss = FindDropTargetAt(new Point(origin.X + 20, origin.Y + realHeight + 200));

        string layout = $"vm=({vm.X:0},{vm.Y:0}) origin=({origin.X:0},{origin.Y:0}) " +
                        $"size=({body.ActualWidth:0}x{body.ActualHeight:0}) " +
                        $"nodes={_nodes.Count} canvas={ConnectionCanvas.ActualWidth:0}x{ConnectionCanvas.ActualHeight:0}";

        bool pass = ReferenceEquals(hitMiddle, vm)
            && ReferenceEquals(hitLow, vm)
            && miss == null
            && realHeight > nominal + 40;

        string drop = $"Tall-node drop: {layout} | origin-hit -> {(ReferenceEquals(hitOrigin, vm) ? vm.Title : "miss")} | " +
                      $"centre -> {(ReferenceEquals(hitMiddle, vm) ? vm.Title : "miss")} | " +
                      $"bottom -> {(ReferenceEquals(hitLow, vm) ? vm.Title : "miss")} | " +
                      $"below -> {(miss == null ? "miss" : miss.Title)} -> {(pass ? "PASS" : "FAIL")}";

        // ---- Zoom ----
        UpdateGraphExtent();
        double hostWidthBefore = GraphHost.Width;

        var pointer = new Point(320, 180);
        double worldBefore = (GraphScroll.HorizontalOffset + pointer.X) / _zoomTransform.ScaleX;

        ZoomAt(pointer, 1.25);
        double zoomedScale = _zoomTransform.ScaleX;

        // The ScrollViewer applies the anchored offset on the next dispatcher
        // pass, so let that happen before measuring.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);

        double worldAfter = (GraphScroll.HorizontalOffset + pointer.X) / _zoomTransform.ScaleX;

        string zoomDiag = $"diag[host={GraphHost.Width:0} layer={GraphLayer.Width:0} " +
                          $"extent={GraphScroll.ExtentWidth:0} viewport={GraphScroll.ViewportWidth:0} " +
                          $"offset={GraphScroll.HorizontalOffset:0}]";

        // Zooming out must reveal more of the world, and the canvas must be
        // bigger than the nodes so the user can pan around.
        double worldWidth = GraphLayer.Width;
        bool roomToPan = worldWidth > (vm.X + vm.Width) + 500;

        ZoomAt(pointer, 1 / 1.25);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
        double restoredScale = _zoomTransform.ScaleX;

        bool zoomPass = Math.Abs(zoomedScale - 1.25) < 0.01
            && Math.Abs(worldAfter - worldBefore) < 2
            && Math.Abs(restoredScale - 1.0) < 0.01
            && roomToPan
            && GraphHost.Width > 0;

        string zoom = $"Zoom: scale {zoomedScale:0.00} -> restored {restoredScale:0.00}, " +
                      $"pointer stayed on world x {worldBefore:0.0} -> {worldAfter:0.0}, " +
                      $"world width {worldWidth:0} (host was {hostWidthBefore:0}), room to pan {roomToPan} " +
                      $"{zoomDiag} -> {(zoomPass ? "PASS" : "FAIL")}";

        // ---- Click feedback ----
        _sounds.PlayClick();
        bool clickPass = _sounds.DeviceReady && _sounds.Enabled;

        string click = $"Click sound: device ready {_sounds.DeviceReady}, enabled {_sounds.Enabled} " +
                       $"-> {(clickPass ? "PASS" : "FAIL")}";

        return $"{drop} | {zoom} | {click}";
    }
}
