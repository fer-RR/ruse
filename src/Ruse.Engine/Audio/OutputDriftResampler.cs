using System;
using System.Threading;

namespace Ruse.App.Audio;

internal sealed class OutputDriftResampler
{
    private const int CapacityFrames = 8192;
    private readonly float[] _buffer = new float[CapacityFrames * 2];
    private int _count;
    private double _position;
    private int _resetRequested;

    public void Reset()
    {
        _count = 0;
        _position = 0;
        Interlocked.Exchange(ref _resetRequested, 0);
    }

    public void RequestReset() => Interlocked.Exchange(ref _resetRequested, 1);

    public void Process(FloatRingBuffer input, Span<float> output, int channels, double ratio)
    {
        if (Interlocked.Exchange(ref _resetRequested, 0) != 0)
        {
            _count = 0;
            _position = 0;
        }

        int outFrames = output.Length / Math.Max(1, channels);
        if (outFrames <= 0)
        {
            return;
        }

        ratio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0.99, 1.01) : 1.0;
        int needed = (int)Math.Ceiling(outFrames * ratio) + 2;
        int room = CapacityFrames - _count;
        if (room > 0)
        {
            int want = Math.Min(needed - _count, room);
            if (want > 0)
            {
                int read = input.Read(_buffer.AsSpan(_count * channels, want * channels));
                _count += read / channels;
            }
        }

        if (_count < 2)
        {
            output.Clear();
            return;
        }

        int produced = 0;
        double position = _position;
        for (; produced < outFrames; produced++)
        {
            int index = (int)position;
            if (index + 1 >= _count)
            {
                break;
            }

            float fraction = (float)(position - index);
            int first = index * channels;
            int second = first + channels;
            int target = produced * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                float a = _buffer[first + channel];
                float b = _buffer[second + channel];
                output[target + channel] = a + ((b - a) * fraction);
            }

            position += ratio;
        }

        if (produced < outFrames)
        {
            output[(produced * channels)..].Clear();
        }

        int consumed = Math.Min((int)position, _count - 1);
        if (consumed > 0)
        {
            int retain = (_count - consumed) * channels;
            Buffer.BlockCopy(
                _buffer,
                consumed * channels * sizeof(float),
                _buffer,
                0,
                retain * sizeof(float));
            _count -= consumed;
            position -= consumed;
        }

        _position = position;
    }
}
