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

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? ProcessName : $"{DisplayName} ({ProcessName})";
}

public static class AudioSessionEnumerator
{
    public static IReadOnlyList<AudioSessionInfo> GetRenderSessions()
    {
        var result = new Dictionary<uint, AudioSessionInfo>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            SessionCollection? sessions = null;
            try
            {
                sessions = device.AudioSessionManager.Sessions;
            }
            catch
            {
                continue;
            }

            if (sessions == null)
            {
                continue;
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                uint pid = session.GetProcessID;
                if (pid == 0 || pid == (uint)Environment.ProcessId)
                {
                    continue;
                }

                string name = TryGetProcessName(pid);
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
                    };
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
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return $"pid {pid}";
        }
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
