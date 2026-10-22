using NAudio.Wave;
using NAudio.Wave.Asio;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Describes one ASIO driver discovered on this machine.
/// </summary>
/// <remarks>
/// ASIO drivers are exclusive: while Ruse holds one, no other application can
/// open it. That is the trade-off for the sample-accurate, low-latency path
/// professional interfaces offer.
/// </remarks>
public sealed class AsioDriverInfo
{
    /// <summary>Driver name as the ASIO registry reports it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Number of input channels the driver exposes.</summary>
    public int InputChannelCount { get; init; }

    /// <summary>Number of output channels the driver exposes.</summary>
    public int OutputChannelCount { get; init; }

    /// <summary>The driver's current sample rate.</summary>
    public int SampleRate { get; init; }

    /// <summary>Preferred buffer size in frames.</summary>
    public int BufferSize { get; init; }

    /// <summary>Output latency in frames.</summary>
    public int OutputLatencySamples { get; init; }

    /// <summary>Input latency in frames.</summary>
    public int InputLatencySamples { get; init; }

    /// <summary>Per-channel input names, as the driver reports them.</summary>
    public string[] InputChannelNames { get; init; } = Array.Empty<string>();

    /// <summary>Per-channel output names, as the driver reports them.</summary>
    public string[] OutputChannelNames { get; init; } = Array.Empty<string>();

    /// <summary>Stable id used in presets and the device list.</summary>
    public string Id => AsioCatalog.IdFor(Name);
}

/// <summary>
/// Finds the ASIO drivers installed on this machine.
/// </summary>
public static class AsioCatalog
{
    /// <summary>Prefix that marks a device id as belonging to an ASIO driver.</summary>
    public const string IdPrefix = "asio:";

    public static string IdFor(string driverName) => IdPrefix + driverName;

    /// <summary>True when the id refers to an ASIO driver rather than a WASAPI endpoint.</summary>
    public static bool IsAsioId(string deviceId) =>
        deviceId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts the driver name from an ASIO device id.</summary>
    public static string NameFromId(string deviceId) =>
        deviceId.Length > IdPrefix.Length ? deviceId[IdPrefix.Length..] : deviceId;

    /// <summary>
    /// Enumerates the installed ASIO drivers and reads their capabilities.
    /// Drivers that refuse to open (no clock, already in use) are skipped.
    /// </summary>
    public static IReadOnlyList<AsioDriverInfo> Discover()
    {
        var results = new List<AsioDriverInfo>();

        string[] names;
        try
        {
            names = AsioDevice.GetDriverNames();
        }
        catch (Exception ex)
        {
            AppLog.Warning("ASIO driver enumeration failed: " + ex.Message);
            return results;
        }

        foreach (string name in names)
        {
            AsioDevice? device = null;
            try
            {
                device = AsioDevice.Open(name);
                var caps = device.Capabilities;

                var inputNames = new List<string>();
                var outputNames = new List<string>();
                foreach (var info in caps.InputChannelInfos ?? Array.Empty<AsioChannelInfo>())
                {
                    inputNames.Add(string.IsNullOrWhiteSpace(info.name) ? $"In {info.channel + 1}" : info.name);
                }

                foreach (var info in caps.OutputChannelInfos ?? Array.Empty<AsioChannelInfo>())
                {
                    outputNames.Add(string.IsNullOrWhiteSpace(info.name) ? $"Out {info.channel + 1}" : info.name);
                }

                results.Add(new AsioDriverInfo
                {
                    Name = name,
                    InputChannelCount = caps.NbInputChannels,
                    OutputChannelCount = caps.NbOutputChannels,
                    SampleRate = (int)Math.Round(caps.SampleRate),
                    BufferSize = caps.BufferPreferredSize,
                    OutputLatencySamples = caps.OutputLatency,
                    InputLatencySamples = caps.InputLatency,
                    InputChannelNames = inputNames.ToArray(),
                    OutputChannelNames = outputNames.ToArray(),
                });
            }
            catch (Exception ex)
            {
                // A driver with no clock (a virtual one whose host is closed)
                // refuses to open. That is not fatal: the rest still work.
                AppLog.Info($"ASIO driver '{name}' could not be opened: {ex.Message}");
            }
            finally
            {
                device?.Dispose();
            }
        }

        return results;
    }
}
