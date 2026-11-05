namespace Ruse.App.Audio;

/// <summary>
/// Tuned feedback delay with a fractional read and a one-pole damping filter.
/// Short noise or voice bursts ring at the set pitch like a plucked string.
/// </summary>
public sealed class StringResonatorProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "string-resonator";

    private const int SampleRate = 48000;
    private const int MaxChannels = 2;
    private const int BufferSize = 4096;

    private readonly AudioParameter[] _parameters;
    private readonly double[,] _buffer = new double[MaxChannels, BufferSize];
    private readonly int[] _write = new int[MaxChannels];
    private readonly double[] _lowPass = new double[MaxChannels];

    private volatile float _frequency = 220f;
    private volatile float _decay = 80f;
    private volatile float _brightness = 70f;
    private volatile float _mix = 1f;
    private volatile float _level = 1f;

    private double _currentFrequency = 220.0;
    private float _currentDecay = 80f;
    private float _currentBrightness = 70f;
    private float _currentMix = 1f;
    private float _currentLevel = 1f;
    private bool _primed;

    public StringResonatorProcessor()
    {
        _parameters =
        [
            new AudioParameter
            {
                Name = "Frequency",
                Min = 20f,
                Max = 4000f,
                Default = 220f,
                Unit = "Hz",
                Logarithmic = true,
                Get = () => _frequency,
                Set = value => _frequency = Math.Clamp(value, 20f, 4000f),
            },
            new AudioParameter
            {
                Name = "Decay",
                Min = 0f,
                Max = 99f,
                Default = 80f,
                Unit = "%",
                Get = () => _decay,
                Set = value => _decay = Math.Clamp(value, 0f, 99f),
            },
            new AudioParameter
            {
                Name = "Brightness",
                Min = 0f,
                Max = 100f,
                Default = 70f,
                Unit = "%",
                Get = () => _brightness,
                Set = value => _brightness = Math.Clamp(value, 0f, 100f),
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
    public string DisplayName => "String resonator";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0)
        {
            return;
        }

        int channels = Math.Clamp(samples.Length / frames, 1, MaxChannels);
        double targetFrequency = _frequency;
        if (!_primed)
        {
            _currentFrequency = targetFrequency;
            _primed = true;
        }

        _currentDecay = ParameterSmoothing.Move(_currentDecay, _decay, frames);
        _currentBrightness = ParameterSmoothing.Move(_currentBrightness, _brightness, frames);
        _currentMix = ParameterSmoothing.Move(_currentMix, _mix, frames);
        _currentLevel = ParameterSmoothing.Move(_currentLevel, _level, frames);
        double feedback = (_currentDecay / 100.0) * 0.99;
        double cutoff = 300.0 * Math.Pow(40.0, _currentBrightness / 100.0);
        double damping = 1.0 - Math.Exp(-(2.0 * Math.PI * cutoff) / SampleRate);
        double mix = _currentMix;
        double level = _currentLevel;

        for (int frame = 0; frame < frames; frame++)
        {
            _currentFrequency += (targetFrequency - _currentFrequency) * ParameterSmoothing.FastAlpha;
            double delaySamples = Math.Clamp(SampleRate / _currentFrequency, 2.0, BufferSize - 2.0);
            int delay = (int)Math.Floor(delaySamples);
            double fraction = delaySamples - delay;
            int index = frame * channels;

            for (int channel = 0; channel < channels; channel++)
            {
                int write = _write[channel];
                int read = write - delay;
                if (read < 0)
                {
                    read += BufferSize;
                }

                int older = read - 1;
                if (older < 0)
                {
                    older += BufferSize;
                }

                double delayed = _buffer[channel, read] +
                                 ((_buffer[channel, older] - _buffer[channel, read]) * fraction);
                _lowPass[channel] += (delayed - _lowPass[channel]) * damping;
                double wet = _lowPass[channel] * level;
                double input = samples[index + channel];
                double output = input + ((wet - input) * mix);

                double stored = input + (_lowPass[channel] * feedback);
                if (!double.IsFinite(stored) || !double.IsFinite(output))
                {
                    stored = 0.0;
                    output = 0.0;
                    _lowPass[channel] = 0.0;
                }

                _buffer[channel, write] = Math.Clamp(DenormalMath.Flush(stored), -4.0, 4.0);
                _write[channel] = (write + 1) % BufferSize;
                samples[index + channel] = (float)Math.Clamp(output, -4.0, 4.0);
            }
        }

        DenormalMath.Flush(_lowPass);
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        Array.Clear(_write);
        Array.Clear(_lowPass);
        _currentFrequency = _frequency;
        _currentDecay = _decay;
        _currentBrightness = _brightness;
        _currentMix = _mix;
        _currentLevel = _level;
        _primed = false;
    }
}
