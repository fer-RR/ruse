namespace Ruse.App.Audio;

/// <summary>
/// Small radix-2 FFT used by the display taps. It keeps the latest window of
/// mono audio, applies a Hann window and produces log-spaced band levels.
/// </summary>
internal sealed class FftAnalyzer
{
    public const int DefaultSampleRate = 48000;
    private readonly int _sampleRate;
    public const int FftSize = 4096;
    public const int Bands = 256;
    internal const int HopSize = 1536;

    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imag = new float[FftSize];
    private readonly float[] _levels = new float[Bands];
    private readonly int[] _reverse = new int[FftSize];
    private readonly (int Start, int End)[] _edges = new (int, int)[Bands];

    private int _write;
    private int _filled;
    private int _since;
    private float _peakHz;

    public FftAnalyzer(int sampleRate = DefaultSampleRate)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : DefaultSampleRate;
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FftSize - 1));
        }

        int bits = 0;
        while ((1 << bits) < FftSize) bits++;
        for (int i = 0; i < FftSize; i++)
        {
            int value = i;
            int reversed = 0;
            for (int bit = 0; bit < bits; bit++)
            {
                reversed = (reversed << 1) | (value & 1);
                value >>= 1;
            }

            _reverse[i] = reversed;
        }

        double ratio = 20000.0 / 20.0;
        for (int band = 0; band < Bands; band++)
        {
            double low = 20.0 * Math.Pow(ratio, band / (double)Bands);
            double high = 20.0 * Math.Pow(ratio, (band + 1) / (double)Bands);
            int start = Math.Clamp((int)Math.Floor(low * FftSize / _sampleRate), 1, FftSize / 2 - 1);
            int end = Math.Clamp((int)Math.Ceiling(high * FftSize / _sampleRate), start + 1, FftSize / 2);
            _edges[band] = (start, end);
        }
    }

    public ReadOnlySpan<float> Levels => _levels;
    public float PeakHz => _peakHz;

    /// <summary>Adds interleaved stereo audio and returns true when a new frame was analyzed.</summary>
    public bool Add(ReadOnlySpan<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length < frames * 2) return false;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * 2;
            float mono = (samples[index] + samples[index + 1]) * 0.5f;
            _ring[_write] = mono;
            _write++;
            if (_write == FftSize) _write = 0;
            if (_filled < FftSize) _filled++;
            _since++;
        }

        if (_filled < FftSize || _since < HopSize) return false;
        _since = 0;
        Analyze();
        return true;
    }

    private void Analyze()
    {
        int offset = _write;
        int index = offset;
        for (int i = 0; i < FftSize; i++)
        {
            _real[i] = _ring[index] * _window[i];
            _imag[i] = 0f;
            index++;
            if (index == FftSize) index = 0;
        }

        Transform();

        int peakBin = 1;
        double peakPower = 0.0;
        for (int bin = 1; bin < FftSize / 2; bin++)
        {
            double power = ((double)_real[bin] * _real[bin]) +
                           ((double)_imag[bin] * _imag[bin]);
            if (power > peakPower)
            {
                peakPower = power;
                peakBin = bin;
            }
        }

        _peakHz = peakHz(peakBin);
        double powerScale = 16.0 / ((double)FftSize * FftSize);
        for (int band = 0; band < Bands; band++)
        {
            var (start, end) = _edges[band];
            // The peak bin keeps a narrow tone visible in a wide log band; an
            // average would dilute it by the band's bin count at high frequencies.
            double bandPeak = 0.0;
            for (int bin = start; bin < end; bin++)
            {
                double power = powerScale *
                    (((double)_real[bin] * _real[bin]) +
                     ((double)_imag[bin] * _imag[bin]));
                if (power > bandPeak) bandPeak = power;
            }

            _levels[band] = (float)(10.0 * Math.Log10(bandPeak + 1e-12));
        }
    }

    private void Transform()
    {
        for (int i = 0; i < FftSize; i++)
        {
            int j = _reverse[i];
            if (j <= i) continue;
            (_real[i], _real[j]) = (_real[j], _real[i]);
            (_imag[i], _imag[j]) = (_imag[j], _imag[i]);
        }

        for (int length = 2; length <= FftSize; length <<= 1)
        {
            float angle = -2f * MathF.PI / length;
            float wlReal = MathF.Cos(angle);
            float wlImag = MathF.Sin(angle);
            int half = length >> 1;
            for (int start = 0; start < FftSize; start += length)
            {
                float wr = 1f;
                float wi = 0f;
                for (int j = 0; j < half; j++)
                {
                    int even = start + j;
                    int odd = even + half;
                    float tr = wr * _real[odd] - wi * _imag[odd];
                    float ti = wr * _imag[odd] + wi * _real[odd];
                    _real[odd] = _real[even] - tr;
                    _imag[odd] = _imag[even] - ti;
                    _real[even] += tr;
                    _imag[even] += ti;

                    float next = wr * wlReal - wi * wlImag;
                    wi = wr * wlImag + wi * wlReal;
                    wr = next;
                }
            }
        }
    }

    private float peakHz(int bin) => bin * _sampleRate / (float)FftSize;
}
