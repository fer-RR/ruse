using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using OwnVST3Host;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private IReadOnlyList<AsioDriverInfo> _asioDrivers = Array.Empty<AsioDriverInfo>();
    private IReadOnlyList<PluginDescriptor> _vst3Descriptors = Array.Empty<PluginDescriptor>();
    private Task? _vst3ScanTask;

    private void RefreshCatalog()
    {
        try
        {
            RefreshDevices();
            RefreshApps(publish: false, force: true);
            RefreshEffectsAndChains();
            Post("catalog", _catalog);
            SetStatus("Applications and devices refreshed.");
        }
        catch (Exception ex)
        {
            ShowError("Device enumeration failed: " + ex.Message, ex);
        }

        QueueVst3Scan();
    }

    private void QueueVst3Scan()
    {
        if (_vst3ScanTask is { IsCompleted: false } || _disposed)
        {
            return;
        }

        _vst3ScanTask = Task.Run(async () =>
        {
            try
            {
                var found = await PluginScanner.ScanAsync(PluginFormat.Vst3, ScanMode.Fast);
                var descriptors = found
                    .Where(plugin => !plugin.IsInstrument && !string.IsNullOrWhiteSpace(plugin.Identifier))
                    .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (_disposed)
                {
                    return;
                }

                _ = _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _vst3Descriptors = descriptors;
                    _catalog.Vst3Plugins = descriptors.Select(plugin => new CatalogItemDto
                    {
                        Id = plugin.Identifier,
                        Kind = "vst3",
                        Name = string.IsNullOrWhiteSpace(plugin.Name) ? "VST3 plugin" : plugin.Name,
                        Subtitle = string.IsNullOrWhiteSpace(plugin.Vendor) ? "VST3 effect" : $"{plugin.Vendor} · VST3",
                        Details = string.IsNullOrWhiteSpace(plugin.Category) ? plugin.FilePath : plugin.Category,
                    }).ToList();
                    Post("catalog", _catalog);
                    SetStatus($"Found {descriptors.Length} VST3 effect(s).");
                }));
            }
            catch (Exception ex)
            {
                AppLog.Warning("VST3 scan failed: " + ex.Message);
            }
        });
    }

    private PluginDescriptor? FindVst3Descriptor(string identifier) =>
        _vst3Descriptors.FirstOrDefault(plugin =>
            string.Equals(plugin.Identifier, identifier, StringComparison.OrdinalIgnoreCase));

    private void RescanVst3Plugins()
    {
        if (_vst3ScanTask is { IsCompleted: false })
        {
            SetStatus("A VST3 scan is already running.");
            return;
        }

        SetStatus("Scanning for VST3 plugins...");
        QueueVst3Scan();
    }

    private void RefreshDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        _catalog.Outputs = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .OrderBy(device => device.FriendlyName)
            .Select(device => new CatalogItemDto
            {
                Id = device.ID,
                Kind = "output",
                Name = device.FriendlyName,
                Subtitle = "Speaker / headphones - playback",
                Details = "Send routed audio to this device.",
                Direction = "playback",
                Provider = ProviderFromName(device.FriendlyName),
            }).ToList();

        _catalog.Inputs = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .OrderBy(device => device.FriendlyName)
            .Select(device =>
            {
                string provider = ProviderFromName(device.FriendlyName) ?? "";
                bool virtualCapture = IsVirtualCapture(device.FriendlyName);
                return new CatalogItemDto
                {
                    Id = device.ID,
                    Kind = "input",
                    Name = device.FriendlyName,
                    Subtitle = virtualCapture ? "Virtual bus - capture" : "Microphone - capture",
                    Details = virtualCapture
                        ? $"Capture audio leaving {provider} on this bus."
                        : "Capture audio from this input device.",
                    Direction = "capture",
                    Virtual = virtualCapture,
                    Provider = provider,
                    Badge = virtualCapture ? "VIRTUAL" : null,
                };
            }).ToList();

        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _catalog.DefaultOutputId = defaultDevice.ID;
            _catalog.DefaultOutputName = defaultDevice.FriendlyName;
        }
        catch
        {
            _catalog.DefaultOutputId = "";
            _catalog.DefaultOutputName = "";
        }

        if (!string.IsNullOrEmpty(_catalog.DefaultOutputId))
        {
            _catalog.Outputs = _catalog.Outputs
                .OrderByDescending(item => string.Equals(
                    item.Id, _catalog.DefaultOutputId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        try
        {
            _asioDrivers = AsioCatalog.Discover();
            foreach (var driver in _asioDrivers)
            {
                string details = $"ASIO · {driver.InputChannelCount} in / {driver.OutputChannelCount} out, " +
                                 $"{driver.SampleRate} Hz, {driver.BufferSize} frame buffer";
                _catalog.Outputs.Add(new CatalogItemDto
                {
                    Id = driver.Id,
                    Kind = "output",
                    Name = driver.Name,
                    Subtitle = "ASIO output",
                    Details = details,
                    Direction = "playback",
                    Provider = "ASIO",
                    Badge = "ASIO",
                });

                if (driver.InputChannelCount > 0)
                {
                    _catalog.Inputs.Add(new CatalogItemDto
                    {
                        Id = driver.Id,
                        Kind = "input",
                        Name = driver.Name,
                        Subtitle = "ASIO input - first two channels",
                        Details = details,
                        Direction = "capture",
                        Provider = "ASIO",
                        Badge = "ASIO",
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ASIO enumeration failed: " + ex.Message);
        }

        _catalog.Virtuals = VirtualDeviceCatalog.Discover()
            .Select(info => new CatalogItemDto
            {
                Id = info.RenderDeviceId,
                Kind = "virtual",
                Name = info.RenderFriendlyName,
                Subtitle = "Virtual input - playback",
                Details = info.Description,
                MicHint = info.CaptureFriendlyName,
                Direction = "playback",
                Virtual = true,
                Provider = info.Provider,
                Badge = "VIRTUAL",
            }).ToList();

        // A virtual render device is already represented in Virtuals. Keeping it
        // in Outputs too made every Voicemeeter input appear twice.
        var virtualIds = new HashSet<string>(
            _catalog.Virtuals.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        _catalog.Outputs = _catalog.Outputs
            .Where(item => !virtualIds.Contains(item.Id))
            .ToList();

        foreach (var item in _catalog.Outputs.Concat(_catalog.Virtuals))
        {
            item.IsDefault = string.Equals(
                item.Id, _catalog.DefaultOutputId, StringComparison.OrdinalIgnoreCase);
            if (item.IsDefault) item.Badge = "DEFAULT";
        }
    }

    private static string? ProviderFromName(string name)
    {
        if (name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase)) return "Virtual Audio Cable";
        if (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)) return "VB-Cable";
        if (name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
        {
            return "Voicemeeter";
        }

        return null;
    }

    private static bool IsVirtualCapture(string name) =>
        name.Contains("Voicemeeter Out", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    private void RefreshApps(bool publish = true, bool force = false)
    {
        // Background scans are only needed when following playing apps.
        if (!force && !_window.IsActive && !_settings.FollowPlayingApps)
        {
            return;
        }

        _catalog.PerAppCaptureAvailable = ProcessLoopbackCapture.IsSupported;
        _catalog.PerAppCaptureMessage = _catalog.PerAppCaptureAvailable
            ? ""
            : "Per-app capture needs Windows 10 build 20348 or later. Use System audio instead.";
        if (!_catalog.PerAppCaptureAvailable)
        {
            _catalog.Apps = new List<CatalogItemDto>();
            if (publish)
            {
                Post("catalog", _catalog);
            }

            return;
        }

        try
        {
            _catalog.Apps = AudioSessionEnumerator.GetRenderSessions()
                .Select(session => new CatalogItemDto
                {
                    Id = $"{session.ProcessId}:{session.ProcessName}",
                    Kind = "app",
                    Name = session.Label,
                    Subtitle = session.IsActive ? "Active audio session" : "Application",
                    Details = session.Details,
                    ProcessId = session.ProcessId,
                    ProcessName = session.ProcessName,
                    Active = session.IsActive,
                    DeviceId = session.DeviceId,
                    DeviceName = session.DeviceName,
                    IconDataUrl = GetProcessIcon(session.ProcessId),
                }).ToList();

            bool reattached = _settings.FollowPlayingApps && ReattachPlayingApps();
            bool autoAdded = _settings.FollowPlayingApps && AutoAddPlayingApps();
            if (RefreshSourceEndpoints() || reattached || autoAdded)
            {
                PublishGraph();
            }

            if (publish)
            {
                Post("catalog", _catalog);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("Session enumeration failed: " + ex.Message);
        }
    }

    private bool ReattachPlayingApps()
    {
        bool changed = false;
        var active = _catalog.Apps
            .Where(app => app.Active && app.ProcessId.HasValue && !string.IsNullOrEmpty(app.ProcessName))
            .GroupBy(app => app.ProcessName!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in active)
        {
            var processIds = group.Select(app => app.ProcessId!.Value).ToHashSet();
            string appName = group.First().Name;
            var entry = _nodes.Values.FirstOrDefault(entry =>
                entry.Source is { Kind: SourceKind.Application } source &&
                (string.Equals(source.ProcessName, group.Key, StringComparison.OrdinalIgnoreCase) ||
                 (string.IsNullOrEmpty(source.ProcessName) &&
                  string.Equals(source.Title, appName, StringComparison.OrdinalIgnoreCase))));
            if (entry?.Source == null ||
                (entry.Source.ProcessId is uint current && processIds.Contains(current)))
            {
                continue;
            }

            uint processId = processIds.First();
            entry.Source.RetargetApplication(processId, group.Key);
            _graph.EnsureRunning(entry.Source);
            AppLog.Info($"Reattached '{entry.Title}' to process {processId}.");
            SetStatus($"{entry.Title}: reconnected to the playing app.");
            changed = true;
        }

        return changed;
    }

    private bool AutoAddPlayingApps()
    {
        if (string.IsNullOrWhiteSpace(_settings.FollowOutputId))
        {
            return false;
        }

        var target = _catalog.Outputs
            .Concat(_catalog.Virtuals)
            .FirstOrDefault(item => string.Equals(item.Id, _settings.FollowOutputId, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return false;
        }

        var outputEntry = FindOutputByDevice(target.Id);
        if (outputEntry == null)
        {
            double y = 120 + _nodes.Values.Count(entry => entry.Source != null) * 160;
            outputEntry = _catalog.Virtuals.Any(item =>
                string.Equals(item.Id, target.Id, StringComparison.OrdinalIgnoreCase))
                ? AddVirtualOutput(target.Id, 640, y)
                : AddDeviceOutput(target.Id, 640, y);
            if (outputEntry == null)
            {
                return false;
            }
        }

        bool changed = false;
        int index = 0;
        var active = _catalog.Apps
            .Where(app => app.Active && app.ProcessId.HasValue && !string.IsNullOrEmpty(app.ProcessName))
            .GroupBy(app => app.ProcessName!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in active)
        {
            var app = group.First();
            uint processId = app.ProcessId!.Value;
            bool known = _nodes.Values.Any(entry =>
                entry.Source is { Kind: SourceKind.Application } source &&
                (source.ProcessId == processId ||
                 string.Equals(source.ProcessName, group.Key, StringComparison.OrdinalIgnoreCase)));
            if (known)
            {
                continue;
            }

            try
            {
                var source = new SourceNode(app.Name, SourceKind.Application, processId,
                    processName: app.ProcessName);
                var entry = RegisterSource(source, 60, 120 + index * 160);
                ConnectEntries(entry, outputEntry, publish: false);
                index++;
                changed = true;
                SetStatus($"{app.Name}: added and connected to {outputEntry.Title}.");
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Could not auto-add '{app.Name}': {ex.Message}");
            }
        }

        return changed;
    }
    private (string Id, string Name) DefaultRenderEndpoint()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return (device.ID, device.FriendlyName);
        }
        catch
        {
            return ("", "");
        }
    }

    /// <summary>Records the device a source is currently playing through.</summary>
    private void UpdateSourceEndpoint(NodeEntry entry)
    {
        if (entry.Source == null)
        {
            entry.SourceEndpointId = null;
            entry.SourceEndpointName = null;
            return;
        }

        switch (entry.Source.Kind)
        {
            case SourceKind.SystemLoopback:
            {
                var endpoint = DefaultRenderEndpoint();
                entry.SourceEndpointId = string.IsNullOrEmpty(endpoint.Id) ? null : endpoint.Id;
                entry.SourceEndpointName = string.IsNullOrEmpty(endpoint.Name) ? null : endpoint.Name;
                break;
            }
            case SourceKind.Application:
            {
                var app = _catalog.Apps.FirstOrDefault(item => item.ProcessId == entry.Source.ProcessId);
                entry.SourceEndpointId = string.IsNullOrEmpty(app?.DeviceId) ? null : app.DeviceId;
                entry.SourceEndpointName = string.IsNullOrEmpty(app?.DeviceName) ? null : app.DeviceName;
                break;
            }
            default:
                entry.SourceEndpointId = null;
                entry.SourceEndpointName = null;
                break;
        }
    }

    private bool RefreshSourceEndpoints()
    {
        var defaultEndpoint = DefaultRenderEndpoint();
        bool changed = false;
        foreach (var entry in _nodes.Values)
        {
            if (entry.Source == null)
            {
                continue;
            }

            string? id = null;
            string? name = null;
            switch (entry.Source.Kind)
            {
                case SourceKind.SystemLoopback:
                    id = string.IsNullOrEmpty(defaultEndpoint.Id) ? null : defaultEndpoint.Id;
                    name = string.IsNullOrEmpty(defaultEndpoint.Name) ? null : defaultEndpoint.Name;
                    break;
                case SourceKind.Application:
                {
                    var app = _catalog.Apps.FirstOrDefault(item => item.ProcessId == entry.Source.ProcessId);
                    if (app != null)
                    {
                        id = string.IsNullOrEmpty(app.DeviceId) ? null : app.DeviceId;
                        name = string.IsNullOrEmpty(app.DeviceName) ? null : app.DeviceName;
                    }
                    else
                    {
                        // Keep the last endpoint while the app is silent.
                        id = entry.SourceEndpointId;
                        name = entry.SourceEndpointName;
                    }

                    break;
                }
            }

            if (entry.SourceEndpointId == id && entry.SourceEndpointName == name)
            {
                continue;
            }

            entry.SourceEndpointId = id;
            entry.SourceEndpointName = name;
            changed = true;
        }

        return changed;
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;
    private readonly Dictionary<string, string?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private string? GetProcessIcon(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            string? path = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) return null;
            if (_iconCache.TryGetValue(path, out var cached)) return cached;

            var info = new ShellFileInfo();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(),
                ShgfiIcon | ShgfiLargeIcon);
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
            {
                _iconCache[path] = null;
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                string dataUrl = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
                _iconCache[path] = dataUrl;
                return dataUrl;
            }
            finally
            {
                DestroyIcon(info.Icon);
            }
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path, uint fileAttributes, ref ShellFileInfo info, uint infoSize, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private void RefreshEffectsAndChains()
    {
        _catalog.Effects = AudioProcessorCatalog.All
            .OrderBy(effect => effect.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(effect =>
            {
                var item = new CatalogItemDto
                {
                    Id = effect.TypeId,
                    Kind = "effect",
                    Name = effect.DisplayName,
                    Subtitle = effect.Description,
                    Details = effect.Description,
                };

                var live = effect.Create();
                if (live is IParameterizedProcessor parameterized)
                {
                    item.Parameters = BuildParameterDtos(parameterized);
                }

                return item;
            }).ToList();

        _catalog.Chains = SubgraphLibrary.Chains
            .Select(chain => new CatalogItemDto
            {
                Id = chain.Name,
                Kind = "chain",
                Name = chain.Name,
                Subtitle = chain.Description,
                Details = "Saved processing chain",
                Elements = chain.Elements.Select(element => new ChainElementDto
                {
                    TypeId = element.TypeId,
                    Parameters = new Dictionary<string, float>(element.Parameters),
                }).ToList(),
            }).ToList();
    }

    private static List<ParameterDto> BuildParameterDtos(IParameterizedProcessor parameterized)
    {
        var result = new List<ParameterDto>();
        for (int index = 0; index < parameterized.Parameters.Count; index++)
        {
            var parameter = parameterized.Parameters[index];
            if (parameter.Name is SpectrumProcessor.TapParameterName or SpectrogramProcessor.TapParameterName or TranscriptionProcessor.TapParameterName or DbMeterProcessor.TapParameterName) continue;
            if (parameterized is DbMeterProcessor && parameter.Name == "Orientation") continue;
            result.Add(new ParameterDto
            {
                ProcessorIndex = 0,
                ParameterIndex = index,
                Name = parameter.Name,
                Min = parameter.Min,
                Max = parameter.Max,
                Value = parameter.Get(),
                Unit = parameter.Unit,
                Logarithmic = parameter.Logarithmic,
                IsToggle = parameter.IsToggle,
                IsInteger = parameter.IsInteger,
                IsChoice = parameter.IsChoice,
                Options = parameter.Options,
            });
        }

        return result;
    }
}
