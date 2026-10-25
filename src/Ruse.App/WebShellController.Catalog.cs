using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private IReadOnlyList<AsioDriverInfo> _asioDrivers = Array.Empty<AsioDriverInfo>();

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
    }

    private void RefreshDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        _catalog.Outputs = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new CatalogItemDto
            {
                Id = device.ID,
                Kind = "output",
                Name = device.FriendlyName,
                Subtitle = "WASAPI output",
                Details = "Windows audio output device",
            }).ToList();

        _catalog.Inputs = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new CatalogItemDto
            {
                Id = device.ID,
                Kind = "input",
                Name = device.FriendlyName,
                Subtitle = "WASAPI input",
                Details = "Windows capture device",
            }).ToList();

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
                    Badge = "ASIO",
                });

                if (driver.InputChannelCount > 0)
                {
                    _catalog.Inputs.Add(new CatalogItemDto
                    {
                        Id = driver.Id,
                        Kind = "input",
                        Name = driver.Name,
                        Subtitle = "ASIO input · first two channels",
                        Details = details,
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
                Subtitle = info.Provider,
                Details = info.Description,
                MicHint = info.CaptureFriendlyName,
            }).ToList();
    }

    private void RefreshApps(bool publish = true, bool force = false)
    {
        // Skip periodic session scans while the workspace is inactive.
        if (!force && !_window.IsActive)
        {
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
                    Active = session.IsActive,
                    IconDataUrl = GetProcessIcon(session.ProcessId),
                }).ToList();

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
            if (parameter.Name is WaveformProcessor.TapParameterName or TranscriptionProcessor.TapParameterName) continue;
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
