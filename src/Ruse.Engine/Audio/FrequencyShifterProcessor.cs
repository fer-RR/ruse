namespace Ruse.App.Audio;

/// <summary>
/// Single-sideband frequency shifter. A 12-section allpass network produces the
/// Hilbert pair, then a quadrature oscillator shifts the spectrum by a fixed
/// number of hertz without changing pitch relations.
/// </summary>
public sealed class FrequencyShifterProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "frequency-shifter";

    private const int SampleRate = 48000;
    private const int MaxChannels = 2;
    private const int Sections = 12;

    private static readonly double[] Poles =
    [
        0.3609, 2.7412, 11.1573, 44.7581, 179.6242, 798.4578,
        1.2524, 5.5671, 22.3423, 89.6271, 364.7914, 2770.1114,
    ];

    private readonly AudioParameter[] _parameters;
    private readonly double[] _coefficient = new double[Sections];
    private readonly double[,] _x1 = new double[MaxChannels, Sections];
    private readonly double[,] _y1 = new double[MaxChannels, Sections];

    private volatile float _shift = 100f;
    private volatile float _mix = 1f;
    private double _currentShift = 100.0;
    private double _currentMix = 1.0;
    private double _phase;
    private bool _primed;

    public FrequencyShifterProcessor()
    {
        for (int i = 0; i < Sections; i++)
        {
            double poleFrequency = Poles[i] * 15.0;
            double rc = 1.0 / (2.0 * Math.PI * poleFrequency);
            double alpha = (1.0 / rc) * 0.5 / SampleRate;
            double beta = (1.0 - alpha) / (1.0 + alpha);
            _coefficient[i] = -beta;
        }

        _parameters =
        [
            new AudioParameter
            {
                Name = "Shift",
                Min = -2000f,
                Max = 2000f,
                Default = 100f,
                Unit = "Hz",
                Get = () => _shift,
                Set = value => _shift = Math.Clamp(value, -2000f, 2000f),
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
        ];
    }

    public string TypeId => Id;
    public string DisplayName => "Frequency shifter";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0)
        {
            return;
        }

        int channels = Math.Clamp(samples.Length / frames, 1, MaxChannels);
        double targetShift = _shift;
        double targetMix = _mix;
        if (!_primed)
        {
            _currentShift = targetShift;
            _currentMix = targetMix;
            _primed = true;
        }

        double shiftStep = (targetShift - _currentShift) / frames;
        double mixStep = (targetMix - _currentMix) / frames;
        double shift = _currentShift;
        double mix = _currentMix;
        double increment = 2.0 * Math.PI / SampleRate;
        for (int frame = 0; frame < frames; frame++)
        {
            double cosine = Math.Cos(_phase);
            double sine = Math.Sin(_phase);
            int index = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                double real = Hilbert(channel, samples[index + channel], sineSections: false);
                double imaginary = Hilbert(channel, samples[index + channel], sineSections: true);
                double input = samples[index + channel];
                double shifted = Math.Abs(shift) < 0.01
                    ? input
                    : (real * cosine) - (imaginary * sine);
                samples[index + channel] = (float)Math.Clamp(
                    input + ((shifted - input) * mix), -4.0, 4.0);
            }

            _phase -= shift * increment;
            if (_phase >= 2.0 * Math.PI)
            {
                _phase -= 2.0 * Math.PI;
            }
            else if (_phase < 0.0)
            {
                _phase += 2.0 * Math.PI;
            }

            shift += shiftStep;
            mix += mixStep;
        }

        _currentShift = targetShift;
        _currentMix = targetMix;
        DenormalMath.Flush(_x1);
        DenormalMath.Flush(_y1);
    }

    public void Reset()
    {
        Array.Clear(_x1);
        Array.Clear(_y1);
        _phase = 0.0;
        _currentShift = _shift;
        _currentMix = _mix;
        _primed = false;
    }

    private double Hilbert(int channel, double input, bool sineSections)
    {
        int start = sineSections ? 6 : 0;
        double value = input;
        for (int i = start; i < start + 6; i++)
        {
            double output = (_coefficient[i] * (value - _y1[channel, i])) + _x1[channel, i];
            _x1[channel, i] = value;
            _y1[channel, i] = output;
            value = output;
        }

        return value;
    }
}
