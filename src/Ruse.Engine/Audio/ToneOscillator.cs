namespace Ruse.App.Audio;

public enum ToneWaveform
{
    Sine,
    Square,
    Saw,
    Triangle,
    Noise,
    PinkNoise,
    BrownNoise,
}

/// <summary>
/// Sample generator for the test-tone source. Saw and square use PolyBLEP so
/// high notes do not fold back into the audible band; triangle is integrated
/// from the bandlimited square. Noise modes are white, pink (Paul Kellet) and
/// brown.
/// </summary>
public sealed class ToneOscillator
{
    private const int SampleRate = 48000;
    private const double TwoPi = Math.PI * 2.0;

    private readonly Random _random;
    private readonly double[] _pink = new double[7];
    private double _phase;
    private double _triangle;
    private double _brown;

    public ToneOscillator(int seed = 1)
    {
        _random = new Random(seed);
    }

    public void Reset()
    {
        _phase = 0.0;
        _triangle = 0.0;
        _brown = 0.0;
        Array.Clear(_pink);
    }

    public float Next(ToneWaveform waveform, double frequency)
    {
        double dt = Math.Clamp(frequency / SampleRate, 0.0, 0.49);
        double position = _phase / TwoPi;
        double value = waveform switch
        {
            ToneWaveform.Sine => Math.Sin(_phase),
            ToneWaveform.Saw => Saw(position, dt),
            ToneWaveform.Square => Square(position, dt),
            ToneWaveform.Triangle => Triangle(position, dt),
            ToneWaveform.Noise => White(),
            ToneWaveform.PinkNoise => Pink(),
            ToneWaveform.BrownNoise => Brown(),
            _ => Math.Sin(_phase),
        };

        _phase += TwoPi * dt;
        if (_phase >= TwoPi)
        {
            _phase -= TwoPi;
        }

        return (float)Math.Clamp(value, -1.0, 1.0);
    }

    private double Triangle(double position, double dt)
    {
        _triangle = (_triangle * 0.9995) + (Square(position, dt) * 4.0 * dt);
        return Math.Clamp(_triangle, -1.0, 1.0);
    }

    private static double Saw(double position, double dt)
    {
        return (2.0 * position) - 1.0 - PolyBlep(position, dt);
    }

    private static double Square(double position, double dt)
    {
        double value = position < 0.5 ? 1.0 : -1.0;
        value += PolyBlep(position, dt);
        value -= PolyBlep(Wrap(position + 0.5), dt);
        return value;
    }

    private static double PolyBlep(double position, double dt)
    {
        if (position < dt)
        {
            double t = position / dt;
            return (t + t) - (t * t) - 1.0;
        }

        if (position > 1.0 - dt)
        {
            double t = (position - 1.0) / dt;
            return (t * t) + (t + t) + 1.0;
        }

        return 0.0;
    }

    private static double Wrap(double position) => position - Math.Floor(position);

    private double White() => (_random.NextDouble() * 2.0) - 1.0;

    private double Pink()
    {
        double white = White();
        _pink[0] = (0.99886 * _pink[0]) + (white * 0.0555179);
        _pink[1] = (0.99332 * _pink[1]) + (white * 0.0750759);
        _pink[2] = (0.96900 * _pink[2]) + (white * 0.1538520);
        _pink[3] = (0.86650 * _pink[3]) + (white * 0.3104856);
        _pink[4] = (0.55000 * _pink[4]) + (white * 0.5329522);
        _pink[5] = (-0.7616 * _pink[5]) - (white * 0.0168980);
        double value = _pink[0] + _pink[1] + _pink[2] + _pink[3] +
                       _pink[4] + _pink[5] + _pink[6] + (white * 0.5362);
        _pink[6] = white * 0.115926;
        return value * 0.11;
    }

    private double Brown()
    {
        _brown = (_brown + (White() * 0.02)) * 0.998;
        return _brown * 1.1;
    }
}
