namespace Ruse.App.Audio;

/// <summary>
/// Three-band vowel/formant filter with presets and a morph control. Morph
/// slides from the selected vowel to the next in A-E-I-O-U order.
/// </summary>
public sealed class FormantFilterProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "formant-filter";

    private const int SampleRate = 48000;
    private const int MaxChannels = 2;
    private const int Bands = 3;

    private static readonly string[] VowelNames = ["A", "E", "I", "O", "U"];
    private static readonly double[,] VowelFormants =
    {
        { 730.0, 1090.0, 2440.0 },
        { 530.0, 1840.0, 2480.0 },
        { 270.0, 2290.0, 3010.0 },
        { 570.0, 840.0, 2410.0 },
        { 300.0, 870.0, 2240.0 },
    };
    private static readonly double[] BandGains = [1.0, 0.6, 0.35];
    private static readonly double[] BaseQ = [7.0, 10.0, 13.0];

    private readonly AudioParameter[] _parameters;
    private readonly double[] _b0 = new double[Bands];
    private readonly double[] _b2 = new double[Bands];
    private readonly double[] _a1 = new double[Bands];
    private readonly double[] _a2 = new double[Bands];
    private readonly double[,] _z1 = new double[MaxChannels, Bands];
    private readonly double[,] _z2 = new double[MaxChannels, Bands];

    private volatile float _vowel;
    private volatile float _morph;
    private volatile float _resonance = 60f;
    private volatile float _mix = 1f;
    private volatile float _level = 1f;

    private int _cachedVowel = -1;
    private float _cachedMorph = float.NaN;
    private float _cachedResonance = float.NaN;
    private float _currentMorph;
    private float _currentResonance = 60f;
    private float _currentMix = 1f;
    private float _currentLevel = 1f;

    public FormantFilterProcessor()
    {
        _parameters =
        [
            new AudioParameter
            {
                Name = "Vowel",
                Min = 0f,
                Max = VowelNames.Length - 1,
                Default = 0f,
                IsChoice = true,
                IsInteger = true,
                Options = VowelNames,
                Get = () => _vowel,
                Set = value => _vowel = Math.Clamp(MathF.Round(value), 0f, VowelNames.Length - 1),
            },
            new AudioParameter
            {
                Name = "Morph",
                Min = 0f,
                Max = 100f,
                Default = 0f,
                Unit = "%",
                Get = () => _morph,
                Set = value => _morph = Math.Clamp(value, 0f, 100f),
            },
            new AudioParameter
            {
                Name = "Resonance",
                Min = 0f,
                Max = 100f,
                Default = 60f,
                Unit = "%",
                Get = () => _resonance,
                Set = value => _resonance = Math.Clamp(value, 0f, 100f),
            },
            new AudioParameter
            {
                Name = "Mix",
                Min = 0f,
                Max = 100f,
                Default = 100f,
                Unit = "%",
                Get = () => _mix * 100f,
                Set = value => _mix = Math.Clamp(value / 100f, 0f, 1f),
            },
            new AudioParameter
            {
                Name = "Level",
                Min = 0f,
                Max = 200f,
                Default = 100f,
                Unit = "%",
                Get = () => _level * 100f,
                Set = value => _level = Math.Clamp(value / 100f, 0f, 2f),
            },
        ];
    }

    public string TypeId => Id;
    public string DisplayName => "Formant filter";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0)
        {
            return;
        }

        _currentMorph = ParameterSmoothing.Move(_currentMorph, _morph, frames);
        _currentResonance = ParameterSmoothing.Move(_currentResonance, _resonance, frames);
        _currentMix = ParameterSmoothing.Move(_currentMix, _mix, frames);
        _currentLevel = ParameterSmoothing.Move(_currentLevel, _level, frames);
        UpdateCoefficients();
        int channels = Math.Clamp(samples.Length / frames, 1, MaxChannels);
        double mix = _currentMix;
        double level = _currentLevel;

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                double input = samples[index + channel];
                double wet = 0.0;
                for (int band = 0; band < Bands; band++)
                {
                    double filtered = Biquad(
                        band, input, ref _z1[channel, band], ref _z2[channel, band]);
                    wet += filtered * BandGains[band];
                }

                samples[index + channel] = (float)Math.Clamp(
                    input + (((wet * level) - input) * mix), -4.0, 4.0);
            }
        }

        DenormalMath.Flush(_z1);
        DenormalMath.Flush(_z2);
    }

    public void Reset()
    {
        Array.Clear(_z1);
        Array.Clear(_z2);
        _cachedVowel = -1;
        _cachedMorph = float.NaN;
        _cachedResonance = float.NaN;
        _currentMorph = _morph;
        _currentResonance = _resonance;
        _currentMix = _mix;
        _currentLevel = _level;
    }

    private void UpdateCoefficients()
    {
        int vowel = Math.Clamp((int)MathF.Round(_vowel), 0, VowelNames.Length - 1);
        float morph = _currentMorph;
        float resonance = _currentResonance;
        if (vowel == _cachedVowel &&
            Math.Abs(morph - _cachedMorph) < 1e-4f &&
            Math.Abs(resonance - _cachedResonance) < 1e-4f)
        {
            return;
        }

        _cachedVowel = vowel;
        _cachedMorph = morph;
        _cachedResonance = resonance;
        int next = (vowel + 1) % VowelNames.Length;
        double t = morph / 100.0;
        double qScale = 0.5 + (resonance / 100.0);

        for (int band = 0; band < Bands; band++)
        {
            double centre = VowelFormants[vowel, band] +
                            ((VowelFormants[next, band] - VowelFormants[vowel, band]) * t);
            double q = BaseQ[band] * qScale;
            double w0 = 2.0 * Math.PI * centre / SampleRate;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double a0 = 1.0 + alpha;
            _b0[band] = alpha / a0;
            _b2[band] = -alpha / a0;
            _a1[band] = (-2.0 * Math.Cos(w0)) / a0;
            _a2[band] = (1.0 - alpha) / a0;
        }
    }

    private double Biquad(int band, double input, ref double z1, ref double z2)
    {
        double output = (_b0[band] * input) + z1;
        z1 = -(_a1[band] * output) + z2;
        z2 = (_b2[band] * input) - (_a2[band] * output);
        return output;
    }
}
