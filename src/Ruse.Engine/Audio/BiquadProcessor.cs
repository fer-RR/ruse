namespace Ruse.App.Audio;

/// <summary>The second-order filter shapes a <see cref="BiquadProcessor"/> can take.</summary>
public enum BiquadKind
{
    HighPass,
    LowPass,
    Peak,
    LowShelf,
    HighShelf,
}

/// <summary>
/// A second-order (biquad) filter with per-channel state.
/// </summary>
/// <remarks>
/// NAudio 3.x dropped its concrete filter classes, so the shapes a router
/// actually needs live here. Coefficients follow the standard audio EQ
/// cookbook and are only recomputed when a parameter changes, so the render
/// thread stays allocation- and lock-free.
/// </remarks>
public sealed class BiquadProcessor : IAudioProcessor, IParameterizedProcessor
{
    private const float SampleRate = 48000f;

    private readonly BiquadKind _kind;
    private readonly string _typeId;
    private readonly string _displayName;

    // Parameters. Gain is only used by the peak and shelf shapes.
    private volatile float _frequency;
    private volatile float _gainDb;
    private volatile float _q = 0.70710678f;

    private float _cachedFrequency = float.NaN;
    private float _cachedGain = float.NaN;
    private float _cachedQ = float.NaN;

    private double _b0, _b1, _b2, _a1, _a2;

    // Per-channel delay memory: x1/x2 are the input history, y1/y2 the output.
    private double _x1L, _x2L, _y1L, _y2L;
    private double _x1R, _x2R, _y1R, _y2R;

    public BiquadProcessor(
        BiquadKind kind,
        string typeId,
        string displayName,
        float defaultFrequency,
        string frequencyParameterName,
        bool hasGain = false,
        float defaultGainDb = 0f,
        bool hasQ = false,
        float defaultQ = 0.70710678f)
    {
        _kind = kind;
        _typeId = typeId;
        _displayName = displayName;

        _frequency = defaultFrequency;
        _gainDb = defaultGainDb;
        _q = defaultQ;

        var parameters = new List<AudioParameter>
        {
            new()
            {
                Name = frequencyParameterName,
                Min = 20f,
                Max = 20000f,
                Default = defaultFrequency,
                Unit = "Hz",
                Logarithmic = true,
                Get = () => _frequency,
                Set = v => _frequency = Math.Clamp(v, 20f, 20000f),
            },
        };

        if (hasGain)
        {
            parameters.Add(new AudioParameter
            {
                Name = "Gain",
                Min = -24f,
                Max = 24f,
                Default = defaultGainDb,
                Unit = "dB",
                Get = () => _gainDb,
                Set = v => _gainDb = Math.Clamp(v, -24f, 24f),
            });
        }

        if (hasQ)
        {
            parameters.Add(new AudioParameter
            {
                Name = "Q",
                Min = 0.1f,
                Max = 10f,
                Default = defaultQ,
                Get = () => _q,
                Set = v => _q = Math.Clamp(v, 0.1f, 10f),
            });
        }

        Parameters = parameters.ToArray();
    }

    public string TypeId => _typeId;
    public string DisplayName => _displayName;
    public IReadOnlyList<AudioParameter> Parameters { get; }

    /// <summary>Corner frequency of the filter, in Hz.</summary>
    public float Frequency
    {
        get => _frequency;
        set => _frequency = Math.Clamp(value, 20f, 20000f);
    }

    /// <summary>Boost or cut applied by the peak and shelf shapes, in dB.</summary>
    public float GainDb
    {
        get => _gainDb;
        set => _gainDb = Math.Clamp(value, -24f, 24f);
    }

    /// <summary>Biquads add no latency.</summary>
    public int LatencySamples => 0;

    private void UpdateCoefficients()
    {
        float frequency = _frequency;
        float gainDb = _gainDb;
        float q = _q;

        if (frequency == _cachedFrequency && gainDb == _cachedGain && q == _cachedQ)
        {
            return;
        }

        _cachedFrequency = frequency;
        _cachedGain = gainDb;
        _cachedQ = q;

        double w0 = 2.0 * Math.PI * frequency / SampleRate;
        double cosW = Math.Cos(w0);
        double sinW = Math.Sin(w0);
        double alpha = sinW / (2.0 * q);

        double a0, b0, b1, b2, a1, a2;

        switch (_kind)
        {
            case BiquadKind.LowPass:
                a0 = 1.0 + alpha;
                b0 = (1.0 - cosW) / 2.0;
                b1 = 1.0 - cosW;
                b2 = (1.0 - cosW) / 2.0;
                a1 = -2.0 * cosW;
                a2 = 1.0 - alpha;
                break;

            case BiquadKind.Peak:
            {
                double amp = Math.Pow(10.0, gainDb / 40.0);
                a0 = 1.0 + (alpha / amp);
                b0 = 1.0 + (alpha * amp);
                b1 = -2.0 * cosW;
                b2 = 1.0 - (alpha * amp);
                a1 = -2.0 * cosW;
                a2 = 1.0 - (alpha / amp);
                break;
            }

            case BiquadKind.LowShelf:
            {
                double amp = Math.Pow(10.0, gainDb / 40.0);
                double beta = 2.0 * Math.Sqrt(amp) * alpha;
                a0 = (amp + 1.0) + ((amp - 1.0) * cosW) + beta;
                b0 = amp * ((amp + 1.0) - ((amp - 1.0) * cosW) + beta);
                b1 = 2.0 * amp * ((amp - 1.0) - ((amp + 1.0) * cosW));
                b2 = amp * ((amp + 1.0) - ((amp - 1.0) * cosW) - beta);
                a1 = -2.0 * ((amp - 1.0) + ((amp + 1.0) * cosW));
                a2 = (amp + 1.0) + ((amp - 1.0) * cosW) - beta;
                break;
            }

            case BiquadKind.HighShelf:
            {
                double amp = Math.Pow(10.0, gainDb / 40.0);
                double beta = 2.0 * Math.Sqrt(amp) * alpha;
                a0 = (amp + 1.0) - ((amp - 1.0) * cosW) + beta;
                b0 = amp * ((amp + 1.0) + ((amp - 1.0) * cosW) + beta);
                b1 = -2.0 * amp * ((amp - 1.0) + ((amp + 1.0) * cosW));
                b2 = amp * ((amp + 1.0) + ((amp - 1.0) * cosW) - beta);
                a1 = 2.0 * ((amp - 1.0) - ((amp + 1.0) * cosW));
                a2 = (amp + 1.0) - ((amp - 1.0) * cosW) - beta;
                break;
            }

            default: // HighPass
                a0 = 1.0 + alpha;
                b0 = (1.0 + cosW) / 2.0;
                b1 = -(1.0 + cosW);
                b2 = (1.0 + cosW) / 2.0;
                a1 = -2.0 * cosW;
                a2 = 1.0 - alpha;
                break;
        }

        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public void Process(Span<float> samples, int frames)
    {
        UpdateCoefficients();

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
            double yL = (b0 * xL) + (b1 * _x1L) + (b2 * _x2L) - (a1 * _y1L) - (a2 * _y2L);
            _x2L = _x1L;
            _x1L = xL;
            _y2L = _y1L;
            _y1L = yL;
            samples[i] = (float)yL;

            if (samplesPerFrame > 1 && i + 1 < samples.Length)
            {
                double xR = samples[i + 1];
                double yR = (b0 * xR) + (b1 * _x1R) + (b2 * _x2R) - (a1 * _y1R) - (a2 * _y2R);
                _x2R = _x1R;
                _x1R = xR;
                _y2R = _y1R;
                _y1R = yR;
                samples[i + 1] = (float)yR;
            }
        }

        if (!double.IsFinite(_y1L) || !double.IsFinite(_y2L) ||
            !double.IsFinite(_y1R) || !double.IsFinite(_y2R))
        {
            // Never let a bad block poison the filter forever. The output stage
            // has already replaced the non-finite samples with silence.
            Reset();
        }
    }

    public void Reset()
    {
        _x1L = _x2L = _y1L = _y2L = 0;
        _x1R = _x2R = _y1R = _y2R = 0;
        _cachedFrequency = float.NaN;
        _cachedGain = float.NaN;
        _cachedQ = float.NaN;
    }
}
