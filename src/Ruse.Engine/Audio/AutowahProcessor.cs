namespace Ruse.App.Audio;

/// <summary>
/// Envelope-following band-pass wah. The input envelope sweeps a resonant
/// band-pass between 200 Hz and 4 kHz; sensitivity sets how quickly small
/// signals reach the top of the sweep and range limits the sweep width.
/// </summary>
public sealed class AutowahProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "autowah";

    private const int SampleRate = 48000;
    private const int MaxChannels = 2;
    private const double MinFrequency = 200.0;
    private const double MaxFrequency = 4000.0;
    private const double Resonance = 2.5;

    private readonly AudioParameter[] _parameters;
    private readonly double[] _ic1 = new double[MaxChannels];
    private readonly double[] _ic2 = new double[MaxChannels];

    private volatile float _sensitivity = 60f;
    private volatile float _range = 100f;
    private volatile float _mix = 1f;
    private volatile float _level = 1f;

    private readonly double _envelopeAttack = 1.0 - Math.Exp(-1.0 / (0.005 * SampleRate));
    private readonly double _envelopeRelease = 1.0 - Math.Exp(-1.0 / (0.080 * SampleRate));
    private double _envelope;
    private double _currentSensitivity = 6.0;
    private double _currentTop = MaxFrequency;
    private double _currentMix = 1.0;
    private double _currentLevel = 1.0;

    public AutowahProcessor()
    {
        _parameters =
        [
            new AudioParameter
            {
                Name = "Sensitivity",
                Min = 0f,
                Max = 100f,
                Default = 60f,
                Unit = "%",
                Get = () => _sensitivity,
                Set = value => _sensitivity = Math.Clamp(value, 0f, 100f),
            },
            new AudioParameter
            {
                Name = "Range",
                Min = 0f,
                Max = 100f,
                Default = 100f,
                Unit = "%",
                Get = () => _range,
                Set = value => _range = Math.Clamp(value, 0f, 100f),
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
    public string DisplayName => "Auto-wah";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0)
        {
            return;
        }

        int channels = Math.Clamp(samples.Length / frames, 1, MaxChannels);
        _currentSensitivity = ParameterSmoothing.Move(
            (float)_currentSensitivity, _sensitivity * 0.1f, frames);
        _currentTop = ParameterSmoothing.Move(
            (float)_currentTop,
            (float)(MinFrequency + ((MaxFrequency - MinFrequency) * (_range / 100.0))),
            frames);
        _currentMix = ParameterSmoothing.Move((float)_currentMix, _mix, frames);
        _currentLevel = ParameterSmoothing.Move((float)_currentLevel, _level, frames);
        double sensitivityGain = _currentSensitivity;
        double topFrequency = _currentTop;
        double mix = _currentMix;
        double level = _currentLevel;
        double damping = 1.0 / Resonance;
        double g = 0.0;
        double a1 = 0.0;
        double a2 = 0.0;
        double a3 = 0.0;

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            double peak = 0.0;
            for (int channel = 0; channel < channels; channel++)
            {
                double magnitude = Math.Abs(samples[index + channel]);
                if (magnitude > peak) peak = magnitude;
            }

            double coefficient = peak > _envelope ? _envelopeAttack : _envelopeRelease;
            _envelope += (peak - _envelope) * coefficient;
            if ((frame & 15) == 0)
            {
                // The envelope moves over tens of milliseconds; updating the
                // SVF coefficients every 16 samples saves the per-sample
                // tan/pow without an audible step.
                double amount = Math.Clamp(_envelope * sensitivityGain, 0.0, 1.0);
                double cutoff = MinFrequency * Math.Pow(topFrequency / MinFrequency, amount);
                g = Math.Tan(Math.PI * cutoff / SampleRate);
                a1 = 1.0 / (1.0 + (g * (g + damping)));
                a2 = g * a1;
                a3 = g * a2;
            }

            for (int channel = 0; channel < channels; channel++)
            {
                double input = samples[index + channel];
                double v3 = input - _ic2[channel];
                double v1 = (a1 * _ic1[channel]) + (a2 * v3);
                double v2 = _ic2[channel] + (a2 * _ic1[channel]) + (a3 * v3);
                _ic1[channel] = (2.0 * v1) - _ic1[channel];
                _ic2[channel] = (2.0 * v2) - _ic2[channel];
                double wet = v1 * level;
                samples[index + channel] = (float)Math.Clamp(
                    input + ((wet - input) * mix), -4.0, 4.0);
            }
        }

        DenormalMath.Flush(_ic1);
        DenormalMath.Flush(_ic2);
        _envelope = DenormalMath.Flush(_envelope);
    }

    public void Reset()
    {
        Array.Clear(_ic1);
        Array.Clear(_ic2);
        _envelope = 0.0;
        _currentSensitivity = _sensitivity * 0.1;
        _currentTop = MinFrequency + ((MaxFrequency - MinFrequency) * (_range / 100.0));
        _currentMix = _mix;
        _currentLevel = _level;
    }
}
