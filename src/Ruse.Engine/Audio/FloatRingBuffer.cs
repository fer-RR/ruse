namespace Ruse.App.Audio;

/// <summary>
/// Single-producer / single-consumer float ring buffer. The producer and
/// consumer use separate indices with acquire/release publication, so neither
/// side takes a lock or allocates on the audio path.
/// </summary>
/// <remarks>
/// One slot is kept empty to distinguish full from empty. If the producer
/// outruns the consumer, the newest samples that do not fit are dropped; callers
/// that need drop-oldest behavior should trim on the consumer side with
/// <see cref="Skip"/>.
/// </remarks>
public sealed class FloatRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacityFloats;
    private int _writePos;
    private int _readPos;

    public FloatRingBuffer(int capacityFloats)
    {
        _capacityFloats = Math.Max(1024, capacityFloats);
        _buffer = new float[_capacityFloats];
    }

    /// <summary>Number of floats currently readable. Cheap snapshot.</summary>
    public int AvailableToRead
    {
        get
        {
            int read = Volatile.Read(ref _readPos);
            int write = Volatile.Read(ref _writePos);
            int diff = write - read;
            if (diff < 0)
            {
                diff += _capacityFloats;
            }

            return diff;
        }
    }

    /// <summary>Writes as much of <paramref name="data"/> as fits; returns floats written.</summary>
    public int Write(ReadOnlySpan<float> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        int write = Volatile.Read(ref _writePos);
        int read = Volatile.Read(ref _readPos);
        int used = write - read;
        if (used < 0)
        {
            used += _capacityFloats;
        }

        int free = _capacityFloats - 1 - used;
        if (free <= 0)
        {
            return 0;
        }

        int len = Math.Min(data.Length, free);
        int first = Math.Min(len, _capacityFloats - write);
        data[..first].CopyTo(_buffer.AsSpan(write, first));
        if (len > first)
        {
            data[first..len].CopyTo(_buffer.AsSpan(0, len - first));
        }

        int next = write + len;
        if (next >= _capacityFloats)
        {
            next -= _capacityFloats;
        }

        // Release: sample writes above must be visible before the reader sees
        // the new write index.
        Volatile.Write(ref _writePos, next);
        return len;
    }

    /// <summary>Reads up to the destination length; consumer-side operation.</summary>
    public int Read(Span<float> destination)
    {
        int read = Volatile.Read(ref _readPos);
        int write = Volatile.Read(ref _writePos);
        int available = write - read;
        if (available < 0)
        {
            available += _capacityFloats;
        }

        int toRead = Math.Min(available, destination.Length);
        if (toRead == 0)
        {
            return 0;
        }

        int first = Math.Min(toRead, _capacityFloats - read);
        _buffer.AsSpan(read, first).CopyTo(destination);
        if (toRead > first)
        {
            _buffer.AsSpan(0, toRead - first).CopyTo(destination[first..]);
        }

        int next = read + toRead;
        if (next >= _capacityFloats)
        {
            next -= _capacityFloats;
        }

        // Release: the copies above must complete before the producer can
        // overwrite this space.
        Volatile.Write(ref _readPos, next);
        return toRead;
    }

    /// <summary>Clears both indices. Call only while both sides are stopped.</summary>
    public void Clear()
    {
        Volatile.Write(ref _readPos, 0);
        Volatile.Write(ref _writePos, 0);
    }

    /// <summary>Drops the oldest samples. Consumer-side operation.</summary>
    public int Skip(int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        int read = Volatile.Read(ref _readPos);
        int write = Volatile.Read(ref _writePos);
        int available = write - read;
        if (available < 0)
        {
            available += _capacityFloats;
        }

        int skipped = Math.Min(count, available);
        int next = read + skipped;
        if (next >= _capacityFloats)
        {
            next -= _capacityFloats;
        }

        Volatile.Write(ref _readPos, next);
        return skipped;
    }
}
