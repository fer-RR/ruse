namespace Ruse.App.Audio;

/// <summary>
/// Second-order (biquad) high-pass filter, useful for removing rumble and
/// handling noise. Coefficients are recomputed only when the cutoff changes,
/// so the render thread stays allocation- and lock-free.
/// </summary>
public sealed class HighPassProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "highpass";

    private const float SampleRate = 48000f;
    private const double Q = 0.70710678; // Butterworth response

    private volatile float _cutoff = 80f;
    private float _cachedCutoff = float.NaN;

    private double _b0, _b1, _b2, _a1, _a2;

    private double _x1L, _x2L, _y1L, _y2L;
    private double _x1R, _x2R, _y1R, _y2R;

    public string TypeId => Id;
    public string DisplayName => "High-pass";

    public float Cutoff
    {
        get => _cutoff;
        set => _cutoff = Math.Clamp(value, 20f, 20000f);
    }

    public IReadOnlyList<AudioParameter> Parameters { get; }

    public HighPassProcessor()
    {
        Parameters = new[]
        {
            new AudioParameter
            {
                Name = "Cutoff",
                Min = 20f,
                Max = 20000f,
                Default = 80f,
                Unit = "Hz",
                Logarithmic = true,
                Get = () => _cutoff,
                Set = v => Cutoff = v,
            },
        };
    }

    private void UpdateCoefficients(float cutoff)
    {
        if (cutoff == _cachedCutoff)
        {
            return;
        }

        _cachedCutoff = cutoff;

        double w0 = 2.0 * Math.PI * cutoff / SampleRate;
        double cosW = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * Q);

        double a0 = 1.0 + alpha;
        _b0 = (1.0 + cosW) / 2.0 / a0;
        _b1 = -(1.0 + cosW) / a0;
        _b2 = (1.0 + cosW) / 2.0 / a0;
        _a1 = -2.0 * cosW / a0;
        _a2 = (1.0 - alpha) / a0;
    }

    public void Process(Span<float> samples, int frames)
    {
        float cutoff = _cutoff;
        UpdateCoefficients(cutoff);

        double b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2;

        int samplesPerFrame = frames > 0 ? samples.Length / frames : 2;
        if (samplesPerFrame < 1)
        {
            return;
        }

        for (int f = 0; f < frames; f++)
        {
            int i = f * samplesPerFrame;

            double xL = samples[i];
            double yL = b0 * xL + b1 * _x1L + b2 * _x2L - a1 * _y1L - a2 * _y2L;
            _x2L = _x1L;
            _x1L = xL;
            _y2L = _y1L;
            _y1L = yL;
            samples[i] = (float)yL;

            if (samplesPerFrame > 1 && i + 1 < samples.Length)
            {
                double xR = samples[i + 1];
                double yR = b0 * xR + b1 * _x1R + b2 * _x2R - a1 * _y1R - a2 * _y2R;
                _x2R = _x1R;
                _x1R = xR;
                _y2R = _y1R;
                _y1R = yR;
                samples[i + 1] = (float)yR;
            }
        }
    }

    public void Reset()
    {
        _x1L = _x2L = _y1L = _y2L = 0;
        _x1R = _x2R = _y1R = _y2R = 0;
        _cachedCutoff = float.NaN;
    }
}
