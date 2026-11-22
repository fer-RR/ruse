using System.Diagnostics;
using System.Threading;
using Ruse.App.Diagnostics;

namespace Ruse.App.Audio;

public readonly record struct Vst3MidiEvent(
    byte Status,
    byte Data1,
    byte Data2,
    int SampleOffset = 0);

internal sealed class Vst3MidiQueue
{
    public const int Capacity = 4096;
    private static readonly long MaxAgeTicks = 250 * Stopwatch.Frequency / 1000;

    private readonly Vst3MidiEvent[] _events = new Vst3MidiEvent[Capacity];
    private readonly long[] _timestamps = new long[Capacity];
    private int _head;
    private int _tail;
    private int _dropped;
    private long _lastDropLog;

    public int Dropped => Volatile.Read(ref _dropped);

    public bool TryWrite(Vst3MidiEvent value)
    {
        int tail = _tail;
        int next = (tail + 1) % Capacity;
        if (next == Volatile.Read(ref _head))
        {
            Interlocked.Increment(ref _dropped);
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastDropLog) >= 5000)
            {
                Interlocked.Exchange(ref _lastDropLog, now);
                AppLog.Warning("The VST3 MIDI queue is full; dropping MIDI events.");
            }

            return false;
        }

        _events[tail] = value;
        _timestamps[tail] = Stopwatch.GetTimestamp();
        Volatile.Write(ref _tail, next);
        return true;
    }

    public int Read(Span<Vst3MidiEvent> destination)
    {
        int head = _head;
        int tail = Volatile.Read(ref _tail);
        int count = 0;
        long now = Stopwatch.GetTimestamp();
        while (head != tail && count < destination.Length)
        {
            Vst3MidiEvent value = _events[head];
            long timestamp = _timestamps[head];
            head = (head + 1) % Capacity;
            if (now - timestamp > MaxAgeTicks && !IsRelease(value))
            {
                Interlocked.Increment(ref _dropped);
                continue;
            }

            destination[count++] = value;
        }

        Volatile.Write(ref _head, head);
        return count;
    }

    private static bool IsRelease(Vst3MidiEvent value)
    {
        int kind = value.Status & 0xF0;
        return kind == 0x80 ||
               (kind == 0x90 && value.Data2 == 0) ||
               (kind == 0xB0 && value.Data1 == 64 && value.Data2 < 64);
    }
}
