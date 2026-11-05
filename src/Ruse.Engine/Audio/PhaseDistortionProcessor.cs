namespace Ruse.App.Audio;

/// <summary>
/// Phase-distortion waveshaper in the Casio CZ style. It bends the position of
/// zero crossings inside each cycle, adding bright or hollow harmonics without
/// a normal filter.
/// </summary>
public sealed class PhaseDistortionProcessor : IAudioProcessor, IParameterizedProcessor, INativeProcessor
{
    public const string Id = "phase-distortion";

    private readonly AudioParameter[] _parameters;
    private volatile float _amount;
    private volatile float _fullScale = 1f;
    private volatile bool _bipolar;
    private volatile float _mix = 1f;
    private float _currentAmount;
    private float _currentFullScale = 1f;
    private float _currentMix = 1f;

    public PhaseDistortionProcessor()
    {
        _parameters =
        [
            new AudioParameter
            {
                Name = "Amount",
                Min = -100f,
                Max = 100f,
                Default = 0f,
                Unit = "%",
                Get = () => _amount,
                Set = value => _amount = Math.Clamp(value, -100f, 100f),
            },
            new AudioParameter
            {
                Name = "Full scale",
                Min = 0.1f,
                Max = 2f,
                Default = 1f,
                Get = () => _fullScale,
                Set = value => _fullScale = Math.Clamp(value, 0.1f, 2f),
            },
            new AudioParameter
            {
                Name = "Bipolar",
                Min = 0f,
                Max = 1f,
                Default = 0f,
                IsToggle = true,
                Get = () => _bipolar ? 1f : 0f,
                Set = value => _bipolar = value >= 0.5f,
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
    public string DisplayName => "Phase distortion";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0)
        {
            return;
        }

        _currentAmount = ParameterSmoothing.Move(_currentAmount, _amount, frames);
        _currentFullScale = ParameterSmoothing.Move(_currentFullScale, _fullScale, frames);
        _currentMix = ParameterSmoothing.Move(_currentMix, _mix, frames);
        double maximum = _currentFullScale;
        double amount = _currentAmount / 100.0;
        bool bipolar = _bipolar;
        double mix = _currentMix;
        double half = 0.5 * maximum;
        double midpoint;
        double left;
        double right;
        if (bipolar)
        {
            midpoint = amount >= 1.0 ? maximum :
                       amount <= -1.0 ? -maximum :
                       amount * maximum;
            left = midpoint != -maximum ? maximum / (midpoint + maximum) : 0.0;
            right = midpoint != maximum ? maximum / (maximum - midpoint) : 0.0;
        }
        else
        {
            midpoint = amount >= 1.0 ? maximum :
                       amount <= -1.0 ? 0.0 :
                       (amount + 1.0) * half;
            left = midpoint != 0.0 ? half / midpoint : 0.0;
            right = midpoint != maximum ? half / (maximum - midpoint) : 0.0;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            double input = samples[i];
            double output = bipolar
                ? (input < midpoint ? left * (input - midpoint) : right * (input - midpoint))
                : (input < midpoint ? left * input : (right * (input - midpoint)) + half);
            samples[i] = (float)Math.Clamp(
                input + ((output - input) * mix), -4.0, 4.0);
        }
    }

    public void Reset()
    {
        _currentAmount = _amount;
        _currentFullScale = _fullScale;
        _currentMix = _mix;
    }
}
