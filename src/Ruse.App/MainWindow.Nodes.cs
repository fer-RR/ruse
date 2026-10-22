/// <summary>
/// Nodes part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    private NodeViewModel? AddDeviceSourceNode(DeviceItem item, double? x = null, double? y = null)
    {
        if (item.Asio != null)
        {
            // An ASIO driver captures its first two input channels, which is
            // what the stereo mix can carry.
            var asioSource = new SourceNode(
                item.FriendlyName,
                SourceKind.Asio,
                deviceId: item.Id,
                asioDriver: item.Asio.Name,
                asioChannels: new[] { 0, 1 });

            return AddSourceNode(asioSource, x, y);
        }

        var source = new SourceNode(item.FriendlyName, SourceKind.Device, deviceId: item.Id);
        return AddSourceNode(source, x, y);
    }

    private NodeViewModel? AddSourceNode(SourceNode source, double? x = null, double? y = null, string? id = null)
    {
        _graph.AddSource(source);

        var vm = new NodeViewModel
        {
            Id = id ?? source.Id,
            Title = source.Title,
            Subtitle = source.Kind switch
            {
                SourceKind.Application => "Application",
                SourceKind.Device => "Input device",
                SourceKind.Asio => "ASIO input",
                _ => "System audio",
            },
            Accent = source.Kind == SourceKind.Application ? "#4C8DFF" : "#B06CFF",
            CanBeSource = true,
            X = x ?? (60 + (_nodes.Count * 30)),
            Y = y ?? (60 + (_nodes.Count * 30)),
        };

        vm.VolumeChanged = () =>
        {
            source.Gain = vm.Volume;
            source.Muted = vm.Muted;
        };

        _nodeBacking[vm] = source;
        _nodes.Add(vm);
        BuildNodeVisual(vm, isSource: true);
        SubscribeSourceFaults(source, vm);
        SetStatus($"Added source: {source.Title}");
                PushAddNode(vm);
        return vm;
    }

    private void SubscribeSourceFaults(SourceNode source, NodeViewModel vm)
    {
        source.Faulted += (node, reason) => Dispatcher.BeginInvoke(() =>
        {
            if (source.Kind == SourceKind.Application)
            {
                vm.Subtitle = "Application — stopped";
            }
            else
            {
                vm.Subtitle = "Disconnected";
            }

            AppLog.Error($"Capture source '{node.Title}' stopped: {reason}");
            ShowAlert($"'{node.Title}' stopped: {reason}", isError: true);
            UpdateErrorIndicator();
            SetStatus($"'{node.Title}' stopped: {reason}");
            StopRoutingIfNoOutputsRunning();
        });
    }

    private void SubscribeOutputFaults(OutputNode output, NodeViewModel vm)
    {
        output.Faulted += (node, reason) => Dispatcher.BeginInvoke(() =>
        {
            vm.Subtitle = "Disconnected";
            AppLog.Error($"Output device '{node.Title}' stopped: {reason}");
            ShowAlert($"Output '{node.Title}' stopped: {reason}", isError: true);
            UpdateErrorIndicator();
            SetStatus($"Output '{node.Title}' stopped: {reason}");
            StopRoutingIfNoOutputsRunning();
        });
    }

    private void StopRoutingIfNoOutputsRunning()
    {
        if (!_graph.Outputs.Any(o => o.IsRunning))
        {
            StopAllRouting();
            SetRunState(false);
        }
    }

    private void StopAllRouting()
    {
        _graph.AutoRun = false;
        _stopTrace = Environment.StackTrace;
        foreach (var obj in _nodeBacking.Values)
        {
            switch (obj)
            {
                case SourceNode source:
                    source.Stop();
                    break;
                case OutputNode output:
                    output.Stop();
                    break;
            }
        }
    }

    private NodeViewModel? FindOutputVmByDeviceId(string deviceId)
    {
        foreach (var (vm, obj) in _nodeBacking)
        {
            if (obj is OutputNode existing &&
                string.Equals(existing.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return vm;
            }
        }

        return null;
    }

    private NodeViewModel? AddOutputNode(DeviceItem item, double? x = null, double? y = null, string? id = null)
    {
        var duplicate = FindOutputVmByDeviceId(item.Id);
        if (duplicate != null)
        {
            FlashNode(duplicate);
            SetStatus($"'{duplicate.Title}' is already on the canvas.");
            return duplicate;
        }

        try
        {
            OutputNode output;
            if (item.Asio != null)
            {
                // ASIO drivers are exclusive: opening one takes it away from
                // every other application until the node is stopped.
                output = _graph.AddOutput(item.Asio);
            }
            else
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(item.Id);
                output = _graph.AddOutput(device);
            }

        var vm = new NodeViewModel
        {
            Id = id ?? output.Id,
            Title = output.Title,
            Subtitle = item.Asio != null ? "ASIO output" : "Output device",
                Accent = "#39D98A",
                Kind = NodeKind.Output,
                CanBeSource = false,
                X = x ?? (620 + (_nodes.Count * 20)),
                Y = y ?? (80 + (_nodes.Count * 30)),
            };

            vm.VolumeChanged = () =>
            {
                output.Volume = vm.Volume;
                output.Muted = vm.Muted;
            };

            _nodeBacking[vm] = output;
            _nodes.Add(vm);
            BuildNodeVisual(vm, isSource: false);
            SubscribeOutputFaults(output, vm);
            SetStatus($"Added output: {output.Title}");
                    PushAddNode(vm);
            return vm;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not add the output device.", ex);
            ShowAlert("Could not add the output device: " + ex.Message, isError: true);
            UpdateErrorIndicator();
            return null;
        }
    }

    /// <summary>
    /// Adds an output that renders into memory instead of a device. Used by the
    /// performance test to separate the device cost from the rest of the engine.
    /// </summary>
    private NodeViewModel? AddTestSinkOutput(double? x = null, double? y = null)
    {
        var output = _graph.AddOutputForTest((_, _) => { });

        var vm = new NodeViewModel
        {
            Id = output.Id,
            Title = "Test sink",
            Subtitle = "Output (in-memory)",
            Accent = "#39D98A",
            Kind = NodeKind.Output,
            CanBeSource = false,
            X = x ?? (620 + (_nodes.Count * 20)),
            Y = y ?? (80 + (_nodes.Count * 30)),
        };

        _nodeBacking[vm] = output;
        _nodes.Add(vm);
        BuildNodeVisual(vm, isSource: false);
        return vm;
    }

    private void OnAddSystemClick(object sender, RoutedEventArgs e)
    {
        AddSourceNode(new SourceNode("System audio (everything)", SourceKind.SystemLoopback));
    }

    private NodeViewModel? AddVirtualOutputNode(VirtualOutputInfo info, double? x = null, double? y = null, string? id = null)
    {
        var duplicate = FindOutputVmByDeviceId(info.RenderDeviceId);
        if (duplicate != null)
        {
            FlashNode(duplicate);
            SetStatus($"'{duplicate.Title}' is already on the canvas.");
            return duplicate;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(info.RenderDeviceId);
            var output = _graph.AddOutput(device);

        var vm = new NodeViewModel
        {
            Id = id ?? output.Id,
            Title = info.RenderFriendlyName,
            Subtitle = $"{info.Provider} virtual output",
                Accent = "#E0A33A",
                Kind = NodeKind.Output,
                CanBeSource = false,
                MicHint = info.CaptureFriendlyName,                X = x ?? (640 + (_nodes.Count * 20)),
                Y = y ?? (120 + (_nodes.Count * 30)),
            };

            vm.VolumeChanged = () =>
            {
                output.Volume = vm.Volume;
                output.Muted = vm.Muted;
            };

            _nodeBacking[vm] = output;
            _nodes.Add(vm);
            BuildNodeVisual(vm, isSource: false);
            SubscribeOutputFaults(output, vm);
        SetStatus($"Added virtual output: {info.RenderFriendlyName}. In Discord/OBS select \"{info.CaptureFriendlyName}\" as the microphone.");
        PushAddNode(vm);
        return vm;
        }
        catch (Exception ex)
        {
            SetStatus($"Could not add virtual output: {ex.Message}");
            AppLog.Error("Could not add the virtual output.", ex);
            return null;
        }
    }

    private NodeViewModel? AddModifierNode(AudioProcessorCatalog.Descriptor descriptor, double? x = null, double? y = null)
    {
        var modifier = new ModifierNode(descriptor.DisplayName);
        modifier.Add(descriptor.Create());
        return AddModifierNodeCore(modifier, descriptor.DisplayName, x, y);
    }

    /// <summary>Registers an already-configured modifier node and builds its visual.</summary>
    private NodeViewModel? AddModifierNodeCore(ModifierNode modifier, string title, double? x = null, double? y = null, string? id = null)
    {
        _graph.AddModifier(modifier);

        var vm = new NodeViewModel
        {
            Id = id ?? modifier.Id,
            Title = title,
            Subtitle = "Modifier",
            Accent = "#E0A63A",
            Kind = NodeKind.Modifier,
            CanBeSource = true,
            X = x ?? (330 + (_nodes.Count * 20)),
            Y = y ?? (90 + (_nodes.Count * 30)),
        };

        _nodeBacking[vm] = modifier;
        _nodes.Add(vm);
        BuildModifierVisual(vm, modifier);
        SetStatus($"Added modifier: {title}. Connect a source into it, then it into an output.");
        PushAddNode(vm);
        return vm;
    }

    /// <summary>Builds the compact visual for a modifier: input and output ports plus its parameters.</summary>
}
