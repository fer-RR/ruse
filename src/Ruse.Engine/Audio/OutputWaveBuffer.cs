using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Lock-free byte ring used between the output pump and the endpoint callback.
/// Unlike a managed buffered provider, reads never allocate or block; an
/// underrun is returned as silence instead of leaving the device buffer
/// undefined.
/// </summary>
internal sealed class OutputWaveBuffer : IWaveProvider
{
    private readonly byte[] _buffer;
    private int _read;
    private int _write;
    private long _droppedBytes;

    public OutputWaveBuffer(WaveFormat format, int capacityMs)
    {
        WaveFormat = format;
        int capacity = Math.Max(4096,
            format.AverageBytesPerSecond * Math.Clamp(capacityMs, 20, 500) / 1000);
        _buffer = new byte[capacity];
    }

    public WaveFormat WaveFormat { get; }

    public int BufferedBytes
    {
        get
        {
            int read = Volatile.Read(ref _read);
            int write = Volatile.Read(ref _write);
            int used = write - read;
            return used < 0 ? used + _buffer.Length : used;
        }
    }

    public TimeSpan BufferedDuration =>
        TimeSpan.FromSeconds(BufferedBytes / (double)Math.Max(1, WaveFormat.AverageBytesPerSecond));

    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    public int Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public int Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;

        int write = Volatile.Read(ref _write);
        int read = Volatile.Read(ref _read);
        int used = write - read;
        if (used < 0) used += _buffer.Length;
        int free = _buffer.Length - 1 - used;
        if (free <= 0)
        {
            Interlocked.Add(ref _droppedBytes, data.Length);
            return 0;
        }

        int written = Math.Min(data.Length, free);
        int first = Math.Min(written, _buffer.Length - write);
        data[..first].CopyTo(_buffer.AsSpan(write, first));
        if (written > first)
        {
            data[first..written].CopyTo(_buffer.AsSpan(0, written - first));
        }

        int next = write + written;
        if (next >= _buffer.Length) next -= _buffer.Length;
        Volatile.Write(ref _write, next);
        if (written < data.Length)
        {
            Interlocked.Add(ref _droppedBytes, data.Length - written);
        }

        return written;
    }

    public int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;

        int read = Volatile.Read(ref _read);
        int write = Volatile.Read(ref _write);
        int available = write - read;
        if (available < 0) available += _buffer.Length;

        int copied = Math.Min(available, destination.Length);
        if (copied > 0)
        {
            int first = Math.Min(copied, _buffer.Length - read);
            _buffer.AsSpan(read, first).CopyTo(destination);
            if (copied > first)
            {
                _buffer.AsSpan(0, copied - first).CopyTo(destination[first..]);
            }

            int next = read + copied;
            if (next >= _buffer.Length) next -= _buffer.Length;
            Volatile.Write(ref _read, next);
        }

        // Always give the endpoint a full buffer; missing samples are silence.
        if (copied < destination.Length)
        {
            destination[copied..].Clear();
        }

        return destination.Length;
    }

    public void Clear()
    {
        Volatile.Write(ref _read, 0);
        Volatile.Write(ref _write, 0);
    }
}
