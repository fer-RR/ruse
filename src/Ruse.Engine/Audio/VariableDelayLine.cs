namespace Ruse.App.Audio;

/// <summary>
/// A circular delay whose length can change without reallocating. Used for
/// per-input mixer alignment delays.
/// </summary>
public sealed class VariableDelayLine
{
    private readonly float[] _buffer;
    private readonly int _maxFrames;
    private int _write;
    private int _delayFrames;

    public VariableDelayLine(int maxFrames, int channels)
    {
        _maxFrames = Math.Max(0, maxFrames);
        int frameChannels = Math.Max(1, channels);
        _buffer = new float[Math.Max(1, _maxFrames * frameChannels)];
    }

    public int DelayFrames => Volatile.Read(ref _delayFrames);

    public void SetDelayFrames(int frames) =>
        Volatile.Write(ref _delayFrames, Math.Clamp(frames, 0, _maxFrames));

    public void Reset()
    {
        Array.Clear(_buffer);
        _write = 0;
    }

    public void Process(Span<float> data, int channels)
    {
        int frameChannels = Math.Max(1, channels);
        int delay = Volatile.Read(ref _delayFrames);
        int delaySamples = delay * frameChannels;
        int length = _buffer.Length;

        for (int i = 0; i < data.Length; i++)
        {
            float input = data[i];
            if (delaySamples > 0)
            {
                int read = _write - delaySamples;
                if (read < 0)
                {
                    read += length;
                }

                data[i] = _buffer[read];
            }

            _buffer[_write] = input;
            _write++;
            if (_write >= length)
            {
                _write = 0;
            }
        }
    }
}
