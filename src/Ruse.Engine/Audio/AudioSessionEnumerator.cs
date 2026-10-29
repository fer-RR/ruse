using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Ruse.App.Audio;

public sealed class AudioSessionInfo
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsActive { get; init; }

    /// <summary>Render endpoint the process is currently playing to.</summary>
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;

        public string Label => string.IsNullOrWhiteSpace(DisplayName) ? ProcessName : $"{DisplayName} ({ProcessName})";

        /// <summary>Extra information for the sidebar tooltip.</summary>
        public string Details => IsActive
            ? $"Captures the audio of {ProcessName} (process {ProcessId})"
            : $"{ProcessName} (process {ProcessId}) — nothing is playing right now";
        }

public static class AudioSessionEnumerator
{
    // Caching process names keeps the periodic refresh cheap.
    private static readonly Dictionary<uint, string> ProcessNameCache = new();
    private static readonly string CurrentProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
    private static readonly object ProcessNameLock = new();

    public static IReadOnlyList<AudioSessionInfo> GetRenderSessions()
    {
        var result = new Dictionary<uint, AudioSessionInfo>();
        using var enumerator = new MMDeviceEnumerator();

        // Release COM objects explicitly; finalizers burned CPU at idle.
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    string deviceId = device.ID;
                    string deviceName = device.FriendlyName;

                    // The manager must outlive the session collection.
                    using var manager = device.AudioSessionManager;
                    using var sessions = manager.Sessions;

                    for (int i = 0; i < sessions.Count; i++)
                    {
                        // Session controls are owned by the collection.
                        var session = sessions[i];
                        uint pid = session.GetProcessID;
                        if (pid == 0 || pid == (uint)Environment.ProcessId)
                        {
                            continue;
                        }

                        string name = TryGetProcessName(pid);
                        if (string.Equals(name, CurrentProcessName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string display = TryGetDisplayName(session);
                        bool active = TryIsActive(session);

                        if (result.TryGetValue(pid, out var existing))
                        {
                            result[pid] = new AudioSessionInfo
                            {
                                ProcessId = pid,
                                ProcessName = existing.ProcessName,
                                DisplayName = string.IsNullOrWhiteSpace(existing.DisplayName) ? display : existing.DisplayName,
                                IsActive = existing.IsActive || active,
                                DeviceId = existing.DeviceId.Length == 0 ? deviceId : existing.DeviceId,
                                DeviceName = existing.DeviceName.Length == 0 ? deviceName : existing.DeviceName,
                            };
                        }
                        else
                        {
                            result[pid] = new AudioSessionInfo
                            {
                                ProcessId = pid,
                                ProcessName = name,
                                DisplayName = display,
                                IsActive = active,
                                DeviceId = deviceId,
                                DeviceName = deviceName,
                            };
                        }
                    }
                }
                catch
                {
                    // A device that cannot be queried is simply skipped.
                }
            }
        }

        return result.Values
            .OrderByDescending(s => s.IsActive)
            .ThenBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string TryGetProcessName(uint pid)
    {
        lock (ProcessNameLock)
        {
            if (ProcessNameCache.TryGetValue(pid, out var cached))
            {
                return cached;
            }
        }

        string name;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch
        {
            name = $"pid {pid}";
        }

        lock (ProcessNameLock)
        {
            // Bound the cache.
            if (ProcessNameCache.Count > 256)
            {
                ProcessNameCache.Clear();
            }

            ProcessNameCache[pid] = name;
        }

        return name;
    }

    private static string TryGetDisplayName(AudioSessionControl session)
    {
        try
        {
            return session.DisplayName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryIsActive(AudioSessionControl session)
    {
        try
        {
            return session.State == AudioSessionState.AudioSessionStateActive;
        }
        catch
        {
            return false;
        }
    }
}
