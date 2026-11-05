namespace Ruse.App.Audio;

internal enum PitchTrackerMode
{
    Autocorrelation,
    Amdf,
}

internal interface IPitchTracker
{
    float Confidence { get; }
    void Add(ReadOnlySpan<float> samples, int frames);
    bool TryGetPitch(out float hz);
    void Reset();
}

internal static class PitchTrackerFactory
{
    public static IPitchTracker Create(PitchTrackerMode mode, bool highResolution) =>
        mode == PitchTrackerMode.Amdf
            ? new AmdfPitchTracker(highResolution)
            : new NotePitchTracker(highResolution);
}

/// <summary>
/// Average-magnitude-difference pitch detector for monophonic voice. It is
/// deliberately conservative: the first strong periodic minimum wins, which
/// avoids most octave errors without the latency of a full YIN pass.
/// </summary>
internal sealed class AmdfPitchTracker : IPitchTracker
{
    private readonly int _sampleRate;
    private readonly int _decimation;
    private readonly int _windowSize;
    private readonly int _hop;
    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly float[] _ring;
    private readonly float[] _window;
    private readonly double[] _difference;
    private int _write;
    private int _filled;
    private int _since;
    private float _decimateSum;
    private int _decimateCount;
    private float _pitch;
    private float _confidence;
    private bool _hasPitch;

    public AmdfPitchTracker(bool highResolution = true)
    {
        _sampleRate = highResolution ? 24000 : 12000;
        _decimation = highResolution ? 2 : 4;
        _windowSize = highResolution ? 1536 : 768;
        _hop = highResolution ? 480 : 240;
        _minLag = highResolution ? 24 : 12;
        _maxLag = highResolution ? 600 : 300;
        _ring = new float[_windowSize];
        _window = new float[_windowSize];
        _difference = new double[_maxLag + 1];
    }

    public float Confidence => _confidence;

    public void Add(ReadOnlySpan<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length < frames * 2)
        {
            return;
        }

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * 2;
            _decimateSum += (samples[index] + samples[index + 1]) * 0.5f;
            _decimateCount++;
            if (_decimateCount < _decimation)
            {
                continue;
            }

            _ring[_write] = _decimateSum / _decimation;
            _decimateSum = 0;
            _decimateCount = 0;
            _write++;
            if (_write == _windowSize) _write = 0;
            if (_filled < _windowSize)
            {
                _filled++;
            }

            _since++;
        }

        if (_filled < _windowSize || _since < _hop)
        {
            return;
        }

        _since = 0;
        Detect();
    }

    public bool TryGetPitch(out float hz)
    {
        hz = _pitch;
        return _hasPitch;
    }

    public void Reset()
    {
        Array.Clear(_ring);
        _write = 0;
        _filled = 0;
        _since = 0;
        _decimateSum = 0f;
        _decimateCount = 0;
        _pitch = 0f;
        _confidence = 0f;
        _hasPitch = false;
    }

    private void Detect()
    {
        int ringIndex = _write;
        double energy = 0.0;
        double meanAbsolute = 0.0;
        for (int i = 0; i < _windowSize; i++)
        {
            float sample = _ring[ringIndex];
            _window[i] = sample;
            energy += sample * sample;
            meanAbsolute += Math.Abs(sample);
            ringIndex++;
            if (ringIndex == _windowSize) ringIndex = 0;
        }

        meanAbsolute /= _windowSize;
        if (energy / _windowSize < 1e-7 || meanAbsolute < 1e-5)
        {
            _confidence = 0f;
            _hasPitch = false;
            return;
        }

        double best = double.MaxValue;
        for (int lag = _minLag; lag <= _maxLag; lag++)
        {
            double sum = 0.0;
            int count = _windowSize - lag;
            for (int i = 0; i < count; i++)
            {
                sum += Math.Abs(_window[i] - _window[i + lag]);
            }

            double value = sum / Math.Max(1, count);
            _difference[lag] = value;
            if (value < best)
            {
                best = value;
            }
        }

        double threshold = (best * 1.3) + (meanAbsolute * 0.05);
        int chosen = 0;
        for (int lag = _minLag + 1; lag < _maxLag; lag++)
        {
            if (_difference[lag] > threshold)
            {
                continue;
            }

            if (_difference[lag] <= _difference[lag - 1] &&
                _difference[lag] <= _difference[lag + 1])
            {
                chosen = lag;
                break;
            }
        }

        if (chosen == 0)
        {
            for (int lag = _minLag; lag <= _maxLag; lag++)
            {
                if (_difference[lag] <= best)
                {
                    chosen = lag;
                    break;
                }
            }
        }

        if (chosen <= 0)
        {
            _confidence = 0f;
            _hasPitch = false;
            return;
        }

        double refined = chosen;
        if (chosen > _minLag && chosen < _maxLag)
        {
            double left = _difference[chosen - 1];
            double centre = _difference[chosen];
            double right = _difference[chosen + 1];
            double denominator = left - (2.0 * centre) + right;
            if (Math.Abs(denominator) > 1e-9)
            {
                double offset = 0.5 * (left - right) / denominator;
                refined += Math.Clamp(offset, -1.0, 1.0);
            }
        }

        _pitch = (float)(_sampleRate / refined);
        _confidence = (float)Math.Clamp(1.0 - (best / (meanAbsolute * 2.0)), 0.0, 1.0);
        _hasPitch = float.IsFinite(_pitch) && _pitch > 0f;
    }
}
