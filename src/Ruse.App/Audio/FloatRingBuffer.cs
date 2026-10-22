namespace Ruse.App.Audio;

/// <summary>
/// A lock-free-ish single-producer / single-consumer float ring buffer.
/// Producer writes interleaved float samples, consumer reads them.
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacityFloats;
    private int _writePos;
    private int _readPos;
    private readonly object _sync = new();

    public FloatRingBuffer(int capacityFloats)
    {
        _capacityFloats = Math.Max(1024, capacityFloats);
        _buffer = new float[_capacityFloats];
    }

    /// <summary>Number of floats currently readable. Cheap snapshot, no lock.</summary>
    public int AvailableToRead
    {
        get
        {
            int diff = Volatile.Read(ref _writePos) - Volatile.Read(ref _readPos);
            if (diff < 0)
            {
                diff += _capacityFloats;
            }

            return diff;
        }
    }

    public void Write(ReadOnlySpan<float> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            int len = data.Length;

            // If the incoming block is larger than the buffer, keep only the tail.
            if (len >= _capacityFloats)
            {
                data = data.Slice(len - _capacityFloats);
                len = _capacityFloats;
            }

            int first = Math.Min(len, _capacityFloats - _writePos);
            data.Slice(0, first).CopyTo(_buffer.AsSpan(_writePos, first));
            if (len > first)
            {
                data.Slice(first, len - first).CopyTo(_buffer.AsSpan(0, len - first));
            }

            _writePos += len;
            if (_writePos >= _capacityFloats)
            {
                _writePos -= _capacityFloats;
            }
        }
    }

    public int Read(Span<float> destination)
    {
        lock (_sync)
        {
            int available = _writePos - _readPos;
            if (available < 0)
            {
                available += _capacityFloats;
            }

            int toRead = Math.Min(available, destination.Length);
            if (toRead == 0)
            {
                return 0;
            }

            // Bulk copy in at most two spans (wrap-around aware) instead of
            // sample-by-sample â€” much cheaper on the render thread.
            int first = Math.Min(toRead, _capacityFloats - _readPos);
            _buffer.AsSpan(_readPos, first).CopyTo(destination);
            if (toRead > first)
            {
                _buffer.AsSpan(0, toRead - first).CopyTo(destination.Slice(first));
            }

            _readPos += toRead;
            if (_readPos >= _capacityFloats)
            {
                _readPos -= _capacityFloats;
            }

            return toRead;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _readPos = 0;
            _writePos = 0;
        }
    }

    /// <summary>
    /// Drops the oldest <paramref name="count"/> samples, so a consumer that
    /// fell behind can catch up to the live edge instead of replaying stale
    /// audio forever.
    /// </summary>
    public int Skip(int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        lock (_sync)
        {
            int available = _writePos - _readPos;
            if (available < 0)
            {
                available += _capacityFloats;
            }

            int skipped = Math.Min(count, available);
            _readPos += skipped;
            if (_readPos >= _capacityFloats)
            {
                _readPos -= _capacityFloats;
            }

            return skipped;
        }
    }
}
