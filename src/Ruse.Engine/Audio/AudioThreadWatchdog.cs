using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Lightweight heartbeat watch for realtime audio threads. A stalled thread is
/// reported once; the watchdog never aborts threads or disposes their objects.
/// </summary>
internal static class AudioThreadWatchdog
{
    private const int PollMs = 500;
    private static readonly object Gate = new();
    private static readonly List<Registration> Registrations = new();
    private static Thread? _thread;
    private static readonly AutoResetEvent Wake = new(false);

    public static AudioWatchdogToken Register(string name, TimeSpan timeout, Action onStall)
    {
        var token = new AudioWatchdogToken(name, timeout, onStall);
        lock (Gate)
        {
            Registrations.Add(token.Registration);
            EnsureThread();
        }

        return token;
    }

    internal static void Unregister(Registration registration)
    {
        lock (Gate)
        {
            Registrations.Remove(registration);
        }
    }

    private static void EnsureThread()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(Monitor)
        {
            IsBackground = true,
            Name = "AudioWatchdog",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    private static void Monitor()
    {
        while (true)
        {
            Wake.WaitOne(PollMs);
            Registration[] snapshot;
            lock (Gate)
            {
                snapshot = Registrations.ToArray();
            }

            long now = Environment.TickCount64;
            foreach (Registration registration in snapshot)
            {
                if (registration.Disposed != 0)
                {
                    continue;
                }

                long last = Volatile.Read(ref registration.LastBeat);
                bool stalled = now - last > registration.Timeout.TotalMilliseconds;
                if (!stalled)
                {
                    Volatile.Write(ref registration.StallReported, 0);
                    continue;
                }

                if (Interlocked.Exchange(ref registration.StallReported, 1) != 0)
                {
                    continue;
                }

                AppLog.Error($"Audio thread '{registration.Name}' has not made progress for " +
                             $"{now - last} ms.");
                try
                {
                    registration.OnStall();
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Audio watchdog handler for '{registration.Name}' failed.", ex);
                }
            }
        }
    }

    internal sealed class Registration
    {
        internal Registration(string name, TimeSpan timeout, Action onStall)
        {
            Name = name;
            Timeout = timeout;
            OnStall = onStall;
        }

        internal string Name { get; }
        internal TimeSpan Timeout { get; }
        internal Action OnStall { get; }
        internal long LastBeat;
        internal int StallReported;
        internal int Disposed;
    }
}

internal sealed class AudioWatchdogToken : IDisposable
{
    private int _disposed;

    internal AudioWatchdogToken(string name, TimeSpan timeout, Action onStall)
    {
        Registration = new AudioThreadWatchdog.Registration(name, timeout, onStall);
        Beat();
    }

    internal AudioThreadWatchdog.Registration Registration { get; }

    /// <summary>Marks progress. Cheap enough to call once per render/pump loop.</summary>
    public void Beat() => Volatile.Write(ref Registration.LastBeat, Environment.TickCount64);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref Registration.Disposed, 1);
        AudioThreadWatchdog.Unregister(Registration);
    }
}
