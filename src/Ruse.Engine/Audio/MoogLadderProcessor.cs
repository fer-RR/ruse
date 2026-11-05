namespace Ruse.App.Audio;

/// <summary>
/// Four-pole nonlinear low-pass in the Moog ladder style, with 2x oversampling
/// and a cheap tanh approximation in the feedback path. Cutoff, resonance,
/// drive and output are ramped across each block so live edits do not click.
/// </summary>
public sealed class MoogLadderProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "moog-ladder";

    private const int SampleRate = 48000;
    private const int MaxChannels = 2;
    private const double Thermal = 0.000025;

    private readonly AudioParameter[] _parameters;
    private readonly double[,] _delay = new double[MaxChannels, 6];
    private readonly double[,] _tanhStage = new double[MaxChannels, 3];

    private volatile float _cutoff = 1000f;
    private volatile float _resonance = 0.4f;
    private volatile float _driveDb;
    private volatile float _outputDb;

    private double _currentTune;
    private double _targetTune;
    private double _tuneStep;
    private double _currentRes4;
    private double _targetRes4;
    private double _res4Step;
    private double _currentDrive = 1.0;
    private double _targetDrive = 1.0;
    private double _driveStep;
    private double _currentOutput = 1.0;
    private double _targetOutput = 1.0;
    private double _outputStep;
    private bool _primed;

    public MoogLadderProcessor()
    {
        _parameters =
        [
            new AudioParameter
            {
                Name = "Cutoff",
                Min = 20f,
                Max = 20000f,
                Default = 1000f,
                Unit = "Hz",
                Logarithmic = true,
                Get = () => _cutoff,
                Set = value => _cutoff = Math.Clamp(value, 20f, 20000f),
            },
            new AudioParameter
            {
                Name = "Resonance",
                Min = 0f,
                Max = 1f,
                Default = 0.4f,
                Get = () => _resonance,
                Set = value => _resonance = Math.Clamp(value, 0f, 1f),
            },
            new AudioParameter
            {
                Name = "Drive",
                Min = -12f,
                Max = 24f,
                Default = 0f,
                Unit = "dB",
                Get = () => _driveDb,
                Set = value => _driveDb = Math.Clamp(value, -12f, 24f),
            },
            new AudioParameter
            {
                Name = "Output",
                Min = -24f,
                Max = 12f,
                Default = 0f,
                Unit = "dB",
                Get = () => _outputDb,
                Set = value => _outputDb = Math.Clamp(value, -24f, 12f),
            },
        ];
    }

    public string TypeId => Id;
    public string DisplayName => "Moog ladder";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0)
        {
            return;
        }

        int channels = Math.Clamp(samples.Length / frames, 1, MaxChannels);
        double cutoff = Math.Clamp(_cutoff, 20f, MathF.Min(20000f, SampleRate * 0.45f));
        double resonance = Math.Clamp(_resonance, 0f, 1f);
        ComputeTargets(cutoff, resonance);

        _targetDrive = Math.Pow(10.0, _driveDb / 20.0);
        _targetOutput = Math.Pow(10.0, _outputDb / 20.0);
        if (!_primed)
        {
            _currentTune = _targetTune;
            _currentRes4 = _targetRes4;
            _currentDrive = _targetDrive;
            _currentOutput = _targetOutput;
            _primed = true;
        }

        _tuneStep = (_targetTune - _currentTune) / frames;
        _res4Step = (_targetRes4 - _currentRes4) / frames;
        _driveStep = (_targetDrive - _currentDrive) / frames;
        _outputStep = (_targetOutput - _currentOutput) / frames;

        double tune = _currentTune;
        double res4 = _currentRes4;
        double drive = _currentDrive;
        double output = _currentOutput;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                double value = ProcessChannel(channel, samples[index + channel] * drive, tune, res4);
                samples[index + channel] = (float)Math.Clamp(value * output, -4.0, 4.0);
            }

            tune += _tuneStep;
            res4 += _res4Step;
            drive += _driveStep;
            output += _outputStep;
        }

        _currentTune = _targetTune;
        _currentRes4 = _targetRes4;
        _currentDrive = _targetDrive;
        _currentOutput = _targetOutput;
        DenormalMath.Flush(_delay);
        DenormalMath.Flush(_tanhStage);
    }

    public void Reset()
    {
        Array.Clear(_delay);
        Array.Clear(_tanhStage);
        _primed = false;
        _currentTune = 0.0;
        _targetTune = 0.0;
        _currentRes4 = 0.0;
        _targetRes4 = 0.0;
        _currentDrive = 1.0;
        _targetDrive = 1.0;
        _currentOutput = 1.0;
        _targetOutput = 1.0;
    }

    private void ComputeTargets(double cutoff, double resonance)
    {
        double fc = Math.Clamp(cutoff / SampleRate, 1e-5, 0.45);
        double f = 0.5 * fc;
        double fc2 = fc * fc;
        double fc3 = fc2 * fc;
        double fcr = (1.8730 * fc3) + (0.4955 * fc2) - (0.6490 * fc) + 0.9988;
        double acr = (-3.9364 * fc2) + (1.8409 * fc) + 0.9968;
        _targetTune = (1.0 - Math.Exp(-(2.0 * Math.PI * f * fcr))) / Thermal;
        _targetRes4 = 4.0 * resonance * acr;
    }

    private double ProcessChannel(int channel, double input, double tune, double res4)
    {
        double value = 0.0;
        for (int pass = 0; pass < 2; pass++)
        {
            double x = input - (res4 * _delay[channel, 5]);

            double inputTanh = FastTanh(x * Thermal);
            double stg0 = _delay[channel, 0] + (tune * (inputTanh - _tanhStage[channel, 0]));
            _delay[channel, 0] = stg0;

            double t0 = FastTanh(stg0 * Thermal);
            double stg1 = _delay[channel, 1] + (tune * (t0 - _tanhStage[channel, 1]));
            _delay[channel, 1] = stg1;
            _tanhStage[channel, 0] = t0;

            double t1 = FastTanh(stg1 * Thermal);
            double stg2 = _delay[channel, 2] + (tune * (t1 - _tanhStage[channel, 2]));
            _delay[channel, 2] = stg2;
            _tanhStage[channel, 1] = t1;

            double t2 = FastTanh(stg2 * Thermal);
            double stg3 = _delay[channel, 3] + (tune * (t2 - FastTanh(_delay[channel, 3] * Thermal)));
            _delay[channel, 3] = stg3;
            _tanhStage[channel, 2] = t2;

            _delay[channel, 5] = (stg3 + _delay[channel, 4]) * 0.5;
            _delay[channel, 4] = stg3;
            value = _delay[channel, 5];
        }

        return value;
    }

    private static double FastTanh(double x)
    {
        double sign = 1.0;
        if (x < 0.0)
        {
            sign = -1.0;
            x = -x;
        }

        if (x >= 4.0)
        {
            return sign;
        }

        if (x < 0.5)
        {
            return x * sign;
        }

        return sign * Math.Tanh(x);
    }
}
