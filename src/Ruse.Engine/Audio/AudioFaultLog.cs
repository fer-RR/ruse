using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

/// <summary>
/// Deferred logger for failures detected on the render thread. The renderer
/// only publishes a small preallocated entry; formatting and disk I/O happen on
/// a background thread.
/// </summary>
internal static class AudioFaultLog
{
    private const int Capacity = 32;

    private static readonly Entry[] Entries = new Entry[Capacity];
    private static int _write;
    private static int _read;
    private static int _started;

    private struct Entry
    {
        public string Title;
        public Exception? Exception;
        public bool Warning;
        public bool OutputPump;
        public bool Graph;
    }

    public static void ReportEffectFault(string title, Exception exception) =>
        Enqueue(new Entry { Title = title, Exception = exception });

    public static void ReportEffectWarning(string title) =>
        Enqueue(new Entry { Title = title, Warning = true });

    public static void ReportOutputPumpFault(string title, Exception exception) =>
        Enqueue(new Entry { Title = title, Exception = exception, OutputPump = true });

    public static void ReportGraphFault(Exception exception) =>
        Enqueue(new Entry { Title = "Audio graph", Exception = exception, Graph = true });

    private static void Enqueue(Entry entry)
    {
        EnsureThread();
        int write = Volatile.Read(ref _write);
        int next = (write + 1) % Capacity;
        if (next == Volatile.Read(ref _read))
        {
            return;
        }

        Entries[write] = entry;
        Volatile.Write(ref _write, next);
    }

    private static void EnsureThread()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "AudioFaultLog",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    private static void Loop()
    {
        while (true)
        {
            Thread.Sleep(200);
            int read = Volatile.Read(ref _read);
            int write = Volatile.Read(ref _write);
            while (read != write)
            {
                Entry entry = Entries[read];
                if (entry.Warning)
                {
                    AppLog.Warning($"Effect chain '{entry.Title}' produced non-finite samples; state was reset.");
                }
                else if (entry.Graph)
                {
                    AppLog.Error(
                        "Audio graph render failed; the block was silenced.",
                        entry.Exception);
                }
                else if (entry.OutputPump)
                {
                    AppLog.Error(
                        $"Output '{entry.Title}' pump failed; the block was silenced.",
                        entry.Exception);
                }
                else
                {
                    AppLog.Error(
                        $"Effect chain '{entry.Title}' failed; passing its input through.",
                        entry.Exception);
                }

                read = (read + 1) % Capacity;
                Volatile.Write(ref _read, read);
            }
        }
    }
}
