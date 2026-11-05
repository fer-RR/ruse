namespace Ruse.App.Audio;

/// <summary>
/// Adds high-frequency harmonics without simply boosting the high end. The
/// signal is high-passed, saturated and mixed back over the dry signal.
/// </summary>
public sealed class ExciterProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "exciter";

    private const int SampleRate = 48000;

    private readonly BiquadProcessor _highPass;
    private readonly AudioParameter[] _parameters;

    private volatile float _frequency = 4000f;
    private volatile float _driveDb = 12f;
    private volatile float _amount = 0.5f;
    private float _currentDrive = 3.9810717f;
    private float _currentAmount = 0.5f;
    private bool _primed;

    private float[] _scratch = new float[8192];

    public ExciterProcessor()
    {
        _highPass = new BiquadProcessor(
            BiquadKind.HighPass, "exciter-high-pass", "Exciter high-pass", 4000f, "Frequency", hasQ: true);
        _highPass.Frequency = 4000f;

        _parameters = new[]
        {
            new AudioParameter
            {
                Name = "Frequency",
                Min = 1000f,
                Max = 12000f,
                Default = 4000f,
                Unit = "Hz",
                Logarithmic = true,
                Get = () => _frequency,
                Set = value => _frequency = Math.Clamp(value, 1000f, 12000f),
            },
            new AudioParameter
            {
                Name = "Drive",
                Min = 0f,
                Max = 36f,
                Default = 12f,
                Unit = "dB",
                Get = () => _driveDb,
                Set = value => _driveDb = Math.Clamp(value, 0f, 36f),
            },
            new AudioParameter
            {
                Name = "Amount",
                Min = 0f,
                Max = 1f,
                Default = 0.5f,
                Get = () => _amount,
                Set = value => _amount = Math.Clamp(value, 0f, 1f),
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Exciter";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => _highPass.LatencySamples;

    public void Process(Span<float> samples, int frames)
    {
        if (samples.IsEmpty || frames <= 0)
        {
            return;
        }

        if (_scratch.Length < samples.Length)
        {
            _scratch = new float[samples.Length];
        }

        if (!_primed)
        {
            _currentDrive = MathF.Pow(10f, _driveDb / 20f);
            _currentAmount = _amount;
            _primed = true;
        }

        float targetDrive = MathF.Pow(10f, _driveDb / 20f);
        float targetAmount = _amount;

        samples.CopyTo(_scratch);
        _highPass.Frequency = _frequency;
        _highPass.Process(_scratch.AsSpan(0, samples.Length), frames);

        for (int i = 0; i < samples.Length; i++)
        {
            _currentDrive = ParameterSmoothing.Approach(_currentDrive, targetDrive, ParameterSmoothing.FastAlpha);
            _currentAmount = ParameterSmoothing.Approach(_currentAmount, targetAmount, ParameterSmoothing.FastAlpha);
            samples[i] += MathF.Tanh(_scratch[i] * _currentDrive) * _currentAmount;
        }
    }

    public void Reset()
    {
        _primed = false;
        _highPass.Reset();
    }
}
