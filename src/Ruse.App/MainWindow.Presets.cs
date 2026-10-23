/// <summary>
/// Presets part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    // ---------- Presets ----------

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save routing preset",
            Filter = "Ruse preset (*.ruse.json)|*.ruse.json|JSON (*.json)|*.json|All files (*.*)|*.*",
                FileName = "routing.ruse.json",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var preset = BuildPreset();
            System.IO.File.WriteAllText(dialog.FileName, preset.ToJson());
            SetStatus($"Preset saved: {System.IO.Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
            AppLog.Error("Could not save the preset.", ex);
        }
    }

    private Preset BuildPreset()
    {
        var preset = new Preset { Name = "Ruse routing" };
        var sourceIndex = new Dictionary<SourceNode, int>();
        var outputIndex = new Dictionary<OutputNode, int>();
        var modifierIndex = new Dictionary<ModifierNode, int>();

        foreach (var (vm, obj) in _nodeBacking)
        {
            switch (obj)
            {
                case SourceNode source:
                    sourceIndex[source] = preset.Sources.Count;
                    preset.Sources.Add(new PresetSource
                    {
                        Title = source.Title,
                        Kind = source.Kind.ToString(),
                        ProcessId = source.ProcessId,
                        DeviceId = source.DeviceId,
                        X = vm.X,
                        Y = vm.Y,
                        Volume = vm.Volume,
                        Muted = vm.Muted,
                    });
                    break;
                case OutputNode output:
                    outputIndex[output] = preset.Outputs.Count;
                    preset.Outputs.Add(new PresetOutput
                    {
                        DeviceId = output.DeviceId,
                        Title = output.Title,
                        IsVirtual = vm.MicHint != null,
                        MicHint = vm.MicHint,
                        IsFile = output.IsFileRecorder,
                        FilePath = output.IsFileRecorder ? output.RecordingPath : null,
                        FileFormat = output.IsFileRecorder ? output.RecordingFormat.ToString() : null,
                        X = vm.X,
                        Y = vm.Y,
                        Volume = vm.Volume,
                        Muted = vm.Muted,
                    });
                    break;
                case ModifierNode modifier:
                    modifierIndex[modifier] = preset.Modifiers.Count;
                    preset.Modifiers.Add(new PresetModifier
                    {
                        Title = modifier.Title,
                        X = vm.X,
                        Y = vm.Y,
                        Processors = modifier.Processors.Select(PresetSerializer.SerializeProcessor).ToList(),
                    });
                    break;
            }
        }

        // Modifier wiring (indices resolved against the maps above).
        foreach (var modifier in _graph.Modifiers)
        {
            if (!modifierIndex.TryGetValue(modifier, out int mi))
            {
                continue;
            }

            foreach (var source in modifier.Inputs)
            {
                if (sourceIndex.TryGetValue(source, out int si))
                {
                    preset.SourceModifiers.Add(new PresetSourceModifierLink
                    {
                        SourceIndex = si,
                        ModifierIndex = mi,
                    });
                }
            }

            foreach (var output in modifier.Outputs)
            {
                if (outputIndex.TryGetValue(output, out int oi))
                {
                    preset.ModifierOutputs.Add(new PresetModifierOutputLink
                    {
                        ModifierIndex = mi,
                        OutputIndex = oi,
                    });
                }
            }

            foreach (var next in modifier.Next)
            {
                if (modifierIndex.TryGetValue(next, out int ni))
                {
                    preset.ModifierLinks.Add(new PresetModifierLink
                    {
                        FromModifierIndex = mi,
                        ToModifierIndex = ni,
                    });
                }
            }
        }

        foreach (var output in _graph.Outputs)
        {
            if (!outputIndex.TryGetValue(output, out int oi))
            {
                continue;
            }

            foreach (var route in output.Inputs)
            {
                if (sourceIndex.TryGetValue(route.Source, out int si))
                {
                    preset.Routes.Add(new PresetRoute { SourceIndex = si, OutputIndex = oi, Gain = route.Gain });
                }
            }
        }

        return preset;
    }

    private void OnLoadPresetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load routing preset",
            Filter = "Ruse preset (*.ruse.json)|*.ruse.json|JSON (*.json)|*.json|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var preset = Preset.FromJson(System.IO.File.ReadAllText(dialog.FileName));
            ApplyPreset(preset);
            SetStatus($"Preset loaded: {System.IO.Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Load failed: {ex.Message}");
            AppLog.Error("Could not load the preset.", ex);
        }
    }

    private void ApplyPreset(Preset preset, bool autoRun = true)
    {
        // Loading a preset replaces the whole graph, so the old history no
        // longer describes the canvas.
        _history.Clear();

        OnClearClick(this, new RoutedEventArgs());

        // Clearing the graph stops routing and turns auto-run off. A preset
        // loaded by hand should start on its own, but a session restored at
        // launch waits for the Start button unless the option says otherwise.
        _graph.AutoRun = autoRun;

        var sourceVms = new List<NodeViewModel>();
        var outputVms = new List<NodeViewModel>();

        foreach (var s in preset.Sources)
        {
            if (!Enum.TryParse<SourceKind>(s.Kind, out var kind))
            {
                kind = SourceKind.Application;
            }

            var source = new SourceNode(s.Title, kind, s.ProcessId, s.DeviceId);
            var vm = AddSourceNode(source, s.X, s.Y);
            if (vm != null)
            {
                vm.Volume = s.Volume;
                vm.Muted = s.Muted;
                source.Gain = s.Volume;
                source.Muted = s.Muted;
                sourceVms.Add(vm);
            }
        }

        foreach (var o in preset.Outputs)
        {
            try
            {
                NodeViewModel? vm;
                if (o.IsFile)
                {
                    // Never overwrite a take just because a preset or session
                    // restored a recorder: re-arm it, but wait for Start.
                    vm = AddFileRecorderNode(
                        o.X, o.Y, path: o.FilePath ?? string.Empty,
                        format: AudioFileFormats.Parse(o.FileFormat), autoStart: false);
                }
                else if (AsioCatalog.IsAsioId(o.DeviceId))
                {
                    // ASIO outputs are restored from the driver name: the id
                    // carries it, so no WASAPI lookup is needed.
                    var driverName = AsioCatalog.NameFromId(o.DeviceId);
                    var driver = AsioCatalog.Discover()
                        .FirstOrDefault(d => string.Equals(d.Name, driverName, StringComparison.OrdinalIgnoreCase));

                    vm = driver != null
                        ? AddOutputNode(new DeviceItem(driver), o.X, o.Y)
                        : null;

                    if (driver == null)
                    {
                        SetStatus($"ASIO driver '{driverName}' is not installed; skipped.");
                    }
                }
                else
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var device = enumerator.GetDevice(o.DeviceId);

                    vm = o.IsVirtual
                        ? AddVirtualOutputNode(
                            VirtualDeviceCatalog.ResolveRender(o.DeviceId, o.Title, o.MicHint), o.X, o.Y)
                        : AddOutputNode(new DeviceItem(device), o.X, o.Y);
                }

                if (vm != null)
                {
                    vm.Volume = o.Volume;
                    vm.Muted = o.Muted;
                    outputVms.Add(vm);
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Skipped output '{o.Title}': {ex.Message}");
                AppLog.Warning($"Skipped output '{o.Title}': {ex.Message}");
            }
        }

        foreach (var route in preset.Routes)
        {
            if (route.SourceIndex < 0 || route.SourceIndex >= sourceVms.Count ||
                route.OutputIndex < 0 || route.OutputIndex >= outputVms.Count)
            {
                continue;
            }

            if (_nodeBacking.TryGetValue(sourceVms[route.SourceIndex], out var so) &&
                _nodeBacking.TryGetValue(outputVms[route.OutputIndex], out var oo) &&
                so is SourceNode source && oo is OutputNode output)
            {
                _graph.Connect(source, output, route.Gain);
                AddConnectionVisual(sourceVms[route.SourceIndex], outputVms[route.OutputIndex]);
            }
        }

        // Modifiers, then their wiring, so each link rebuilds the chain correctly.
        var modifierVms = new List<NodeViewModel?>();
        foreach (var m in preset.Modifiers)
        {
            var modifier = new ModifierNode(m.Title);
            foreach (var entry in m.Processors)
            {
                var processor = PresetSerializer.CreateProcessor(entry);
                if (processor != null)
                {
                    modifier.Add(processor);
                }
            }

            if (modifier.Count == 0)
            {
                var fallback = AudioProcessorCatalog.Create(GainProcessor.Id);
                if (fallback != null)
                {
                    modifier.Add(fallback);
                }
            }

            modifierVms.Add(AddModifierNodeCore(modifier, m.Title, m.X, m.Y));
        }

        foreach (var link in preset.SourceModifiers)
        {
            if (TryResolve(sourceVms, link.SourceIndex, out var srcVm) &&
                TryResolve(modifierVms, link.ModifierIndex, out var modVm) &&
                _nodeBacking.TryGetValue(srcVm, out var so) &&
                _nodeBacking.TryGetValue(modVm, out var mo) &&
                so is SourceNode source && mo is ModifierNode modifier)
            {
                _graph.ConnectSourceToModifier(source, modifier);
                AddConnectionVisual(srcVm, modVm);
            }
        }

        foreach (var link in preset.ModifierLinks)
        {
            if (TryResolve(modifierVms, link.FromModifierIndex, out var fromVm) &&
                TryResolve(modifierVms, link.ToModifierIndex, out var toVm) &&
                _nodeBacking.TryGetValue(fromVm, out var fo) &&
                _nodeBacking.TryGetValue(toVm, out var to) &&
                fo is ModifierNode first && to is ModifierNode second)
            {
                _graph.ConnectModifierToModifier(first, second);
                AddConnectionVisual(fromVm, toVm);
            }
        }

        foreach (var link in preset.ModifierOutputs)
        {
            if (TryResolve(modifierVms, link.ModifierIndex, out var modVm) &&
                TryResolve(outputVms, link.OutputIndex, out var outVm) &&
                _nodeBacking.TryGetValue(modVm, out var mo) &&
                _nodeBacking.TryGetValue(outVm, out var oo) &&
                mo is ModifierNode modifier && oo is OutputNode output)
            {
                _graph.ConnectModifierToOutput(modifier, output);
                AddConnectionVisual(modVm, outVm);
            }
        }
    }

    private static bool TryResolve(IReadOnlyList<NodeViewModel?> list, int index, out NodeViewModel vm)
    {
        vm = null!;
        if (index < 0 || index >= list.Count || list[index] == null)
        {
            return false;
        }

        vm = list[index]!;
        return true;
    }
}
