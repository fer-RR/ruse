namespace Ruse.App.Audio;

/// <summary>
/// A fixed-length delay over interleaved samples, used to time-align routes
/// that carry different amounts of effect latency so they sum in phase.
/// </summary>
public sealed class DelayLine
{
    private readonly float[] _buffer;
    private readonly int _stride;
    private int _write;

    public DelayLine(int frames, int channels)
    {
        Frames = Math.Max(0, frames);
        _stride = Math.Max(1, Frames * Math.Max(1, channels));
        _buffer = new float[_stride];
    }

    /// <summary>Delay applied, in frames.</summary>
    public int Frames { get; }

    public void Process(Span<float> data, int channels)
    {
        if (Frames == 0)
        {
            return;
        }

        int stride = Math.Max(1, Frames * Math.Max(1, channels));
        for (int i = 0; i < data.Length; i++)
        {
            float delayed = _buffer[_write];
            _buffer[_write] = data[i];
            data[i] = delayed;
            _write++;
            if (_write >= stride)
            {
                _write = 0;
            }
        }
    }
}
