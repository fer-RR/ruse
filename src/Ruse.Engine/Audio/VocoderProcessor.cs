namespace Ruse.App.Audio;

/// <summary>
/// Sixteen-band channel vocoder. The sidechain is the modulator (usually a
/// voice) and this route is the carrier (synth, pad, noise). Without a
/// sidechain the carrier passes dry.
/// </summary>
public sealed class VocoderProcessor : IAudioProcessor, IParameterizedProcessor, ISidechainProcessor
{
    public const string Id = "vocoder";

    private const int SampleRate = 48000;
    private const int MaxChannels = 2;
    private const int Bands = 16;
    private const double MinFrequency = 100.0;
    private const double MaxFrequency = 8000.0;
    private const double OutputScale = 8.0;
    private const int KeyCapacity = 8192;

    private readonly AudioParameter[] _parameters;
    private readonly double[] _b0 = new double[Bands];
    private readonly double[] _b1 = new double[Bands];
    private readonly double[] _b2 = new double[Bands];
    private readonly double[] _a1 = new double[Bands];
    private readonly double[] _a2 = new double[Bands];
    private readonly double[,] _modZ1 = new double[MaxChannels, Bands];
    private readonly double[,] _modZ2 = new double[MaxChannels, Bands];
    private readonly double[,] _carZ1 = new double[MaxChannels, Bands];
    private readonly double[,] _carZ2 = new double[MaxChannels, Bands];
    private readonly double[,] _envelope = new double[MaxChannels, Bands];
    private readonly float[] _key = new float[KeyCapacity];

    private volatile float _attackMs = 10f;
    private volatile float _releaseMs = 60f;
    private volatile float _bandwidth = 1f;
    private volatile float _mix = 1f;
    private volatile float _level = 1f;

    private double _cachedBandwidth = double.NaN;
    private float _currentAttack = 10f;
    private float _currentRelease = 60f;
    private float _currentBandwidth = 1f;
    private float _currentMix = 1f;
    private float _currentLevel = 1f;
    private int _keyFrames;
    private int _keyChannels = 2;
    private bool _keyReceived;

    public VocoderProcessor()
    {
        _parameters =
        [
            new AudioParameter
            {
                Name = "Attack",
                Min = 1f,
                Max = 100f,
                Default = 10f,
                Unit = "ms",
                Logarithmic = true,
                Get = () => _attackMs,
                Set = value => _attackMs = Math.Clamp(value, 1f, 100f),
            },
            new AudioParameter
            {
                Name = "Release",
                Min = 10f,
                Max = 500f,
                Default = 60f,
                Unit = "ms",
                Logarithmic = true,
                Get = () => _releaseMs,
                Set = value => _releaseMs = Math.Clamp(value, 10f, 500f),
            },
            new AudioParameter
            {
                Name = "Bandwidth",
                Min = 0.5f,
                Max = 2f,
                Default = 1f,
                Get = () => _bandwidth,
                Set = value => _bandwidth = Math.Clamp(value, 0.5f, 2f),
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

        UpdateBands();
    }

    public string TypeId => Id;
    public string DisplayName => "Vocoder";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void SetSidechain(ReadOnlySpan<float> samples, int frames)
    {
        int count = Math.Min(samples.Length, Math.Min(frames * 2, KeyCapacity));
        if (frames <= 0 || count <= 0)
        {
            _keyReceived = false;
            _keyFrames = 0;
            return;
        }

        samples[..count].CopyTo(_key);
        _keyChannels = Math.Max(1, count / frames);
        _keyFrames = Math.Min(frames, count / _keyChannels);
        _keyReceived = true;
    }

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0)
        {
            return;
        }

        if (!_keyReceived)
        {
            return;
        }

        int channels = Math.Clamp(samples.Length / frames, 1, MaxChannels);
        _currentAttack = ParameterSmoothing.Move(_currentAttack, _attackMs, frames);
        _currentRelease = ParameterSmoothing.Move(_currentRelease, _releaseMs, frames);
        _currentBandwidth = ParameterSmoothing.Move(_currentBandwidth, _bandwidth, frames);
        _currentMix = ParameterSmoothing.Move(_currentMix, _mix, frames);
        _currentLevel = ParameterSmoothing.Move(_currentLevel, _level, frames);
        UpdateBands();
        double attack = 1.0 - Math.Exp(-1.0 / ((_currentAttack / 1000.0) * SampleRate));
        double release = 1.0 - Math.Exp(-1.0 / ((_currentRelease / 1000.0) * SampleRate));
        double level = _currentLevel * OutputScale;
        double mix = _currentMix;

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                double carrier = samples[index + channel];
                double modulator = frame < _keyFrames
                    ? _key[(frame * _keyChannels) + Math.Min(channel, _keyChannels - 1)]
                    : 0.0;
                double wet = 0.0;
                for (int band = 0; band < Bands; band++)
                {
                    double modulated = Biquad(
                        band, modulator, ref _modZ1[channel, band], ref _modZ2[channel, band]);
                    double rectified = Math.Abs(modulated);
                    double envelope = _envelope[channel, band];
                    double coefficient = rectified > envelope ? attack : release;
                    envelope += (rectified - envelope) * coefficient;
                    _envelope[channel, band] = envelope;

                    double carrierBand = Biquad(
                        band, carrier, ref _carZ1[channel, band], ref _carZ2[channel, band]);
                    wet += carrierBand * envelope;
                }

                double output = carrier + ((wet * level) - carrier) * mix;
                samples[index + channel] = (float)Math.Clamp(output, -4.0, 4.0);
            }
        }

        _keyReceived = false;
        DenormalMath.Flush(_modZ1);
        DenormalMath.Flush(_modZ2);
        DenormalMath.Flush(_carZ1);
        DenormalMath.Flush(_carZ2);
        DenormalMath.Flush(_envelope);
    }

    public void Reset()
    {
        Array.Clear(_modZ1);
        Array.Clear(_modZ2);
        Array.Clear(_carZ1);
        Array.Clear(_carZ2);
        Array.Clear(_envelope);
        Array.Clear(_key);
        _keyFrames = 0;
        _keyChannels = 2;
        _keyReceived = false;
        _cachedBandwidth = double.NaN;
        _currentAttack = _attackMs;
        _currentRelease = _releaseMs;
        _currentBandwidth = _bandwidth;
        _currentMix = _mix;
        _currentLevel = _level;
    }

    private void UpdateBands()
    {
        double bandwidth = _currentBandwidth;
        if (Math.Abs(bandwidth - _cachedBandwidth) < 1e-6)
        {
            return;
        }

        _cachedBandwidth = bandwidth;
        double ratio = Math.Pow(MaxFrequency / MinFrequency, 1.0 / (Bands - 1));
        for (int band = 0; band < Bands; band++)
        {
            double centre = MinFrequency * Math.Pow(ratio, band);
            double bandWidth = centre * (ratio - 1.0) * bandwidth;
            double q = centre / Math.Max(1e-6, bandWidth);
            double w0 = 2.0 * Math.PI * centre / SampleRate;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double a0 = 1.0 + alpha;
            _b0[band] = alpha / a0;
            _b1[band] = 0.0;
            _b2[band] = -alpha / a0;
            _a1[band] = (-2.0 * Math.Cos(w0)) / a0;
            _a2[band] = (1.0 - alpha) / a0;
        }
    }

    private double Biquad(int band, double input, ref double z1, ref double z2)
    {
        double output = (_b0[band] * input) + z1;
        z1 = (_b1[band] * input) - (_a1[band] * output) + z2;
        z2 = (_b2[band] * input) - (_a2[band] * output);
        return output;
    }
}
