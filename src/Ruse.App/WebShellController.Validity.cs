using System.IO;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private readonly record struct ActiveEndpointIds(
        HashSet<string> Render,
        HashSet<string> Capture);

    private ActiveEndpointIds? _activeEndpointCache;
    private long _activeEndpointCacheTick;
    private const long ActiveEndpointCacheMs = 1000;

    private static ActiveEndpointIds ReadActiveEndpointIds()
    {
        var render = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capture = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    render.Add(device.ID);
                }
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    capture.Add(device.ID);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not enumerate audio endpoints while checking node validity: " + ex.Message);
        }

        return new ActiveEndpointIds(render, capture);
    }

    private ActiveEndpointIds ActiveEndpoints()
    {
        long now = Environment.TickCount64;
        if (_activeEndpointCache is { } cached && now - _activeEndpointCacheTick < ActiveEndpointCacheMs)
        {
            return cached;
        }

        var fresh = ReadActiveEndpointIds();
        _activeEndpointCache = fresh;
        _activeEndpointCacheTick = now;
        return fresh;
    }

    private void InvalidateActiveEndpointCache()
    {
        _activeEndpointCache = null;
        _activeEndpointCacheTick = 0;
    }

    /// <summary>
    /// Test sinks use ids such as "test"; only real WASAPI endpoint ids are
    /// checked against the live device list.
    /// </summary>
    private static bool LooksLikeEndpointId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        (id.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase) ||
         id.StartsWith("{0.0.1.", StringComparison.OrdinalIgnoreCase) ||
         id.Contains("MMDEVAPI", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns true when a node no longer points at a usable device or file.
    /// The same check is used for the red/bypass UI state and for skipping the
    /// node when a session or preset is saved.
    /// </summary>
    private static bool TryGetNodeInvalidReason(
        NodeEntry entry,
        ActiveEndpointIds active,
        out string reason)
    {
        reason = "";

        if (entry.Source is { Kind: SourceKind.Device } source)
        {
            if (string.IsNullOrWhiteSpace(source.DeviceId) ||
                (LooksLikeEndpointId(source.DeviceId) && !active.Capture.Contains(source.DeviceId)))
            {
                reason = "Input device is unavailable.";
                return true;
            }
        }
        else if (entry.Source is { Kind: SourceKind.File } file &&
                 !string.IsNullOrWhiteSpace(file.FilePath) &&
                 !File.Exists(file.FilePath))
        {
            reason = "Audio file is missing.";
            return true;
        }

        if (entry.Output is { } output &&
            !output.IsFileRecorder &&
            !output.IsNetworkOutput &&
            (string.IsNullOrWhiteSpace(output.DeviceId) ||
             (LooksLikeEndpointId(output.DeviceId) && !active.Render.Contains(output.DeviceId))))
        {
            reason = "Output device is unavailable.";
            return true;
        }

        return false;
    }
}
