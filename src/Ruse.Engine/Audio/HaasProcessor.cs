namespace Ruse.App.Audio;

/// <summary>
/// Delays one channel by a few milliseconds. The ear fuses the two channels
/// into one image that feels wider, without the obvious echo of a long delay.
/// </summary>
public sealed class HaasProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "haas";

    private const int SampleRate = 48000;
    private const int MaxDelayMs = 30;
    private const int MaxFrames = SampleRate * MaxDelayMs / 1000;

    private readonly float[] _buffer = new float[MaxFrames];
    private readonly AudioParameter[] _parameters;

    private volatile float _delayMs = 15f;
    private volatile bool _delayRight = true;
    private volatile float _mix = 1f;
    private float _currentDelayMs = 15f;
    private float _currentMix = 1f;
    private bool _primed;

    private int _write;

    public HaasProcessor()
    {
        _parameters = new[]
        {
            new AudioParameter
            {
                Name = "Delay",
                Min = 1f,
                Max = MaxDelayMs,
                Default = 15f,
                Unit = "ms",
                Get = () => _delayMs,
                Set = value => _delayMs = Math.Clamp(value, 1f, MaxDelayMs),
            },
            new AudioParameter
            {
                Name = "Side",
                Min = 0f,
                Max = 1f,
                Default = 1f,
                IsChoice = true,
                IsInteger = true,
                Options = new[] { "Left", "Right" },
                Get = () => _delayRight ? 1f : 0f,
                Set = value => _delayRight = value >= 0.5f,
            },
            new AudioParameter
            {
                Name = "Mix",
                Min = 0f,
                Max = 1f,
                Default = 1f,
                Get = () => _mix,
                Set = value => _mix = Math.Clamp(value, 0f, 1f),
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Haas widener";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        int channels = frames > 0 ? Math.Max(1, samples.Length / frames) : 1;
        if (channels < 2)
        {
            return;
        }

        float targetDelay = _delayMs;
        float targetMix = _mix;
        if (!_primed)
        {
            _currentDelayMs = targetDelay;
            _currentMix = targetMix;
            _primed = true;
        }

        bool right = _delayRight;
        for (int frame = 0; frame < frames; frame++)
        {
            _currentDelayMs = ParameterSmoothing.Approach(_currentDelayMs, targetDelay, ParameterSmoothing.FastAlpha);
            _currentMix = ParameterSmoothing.Approach(_currentMix, targetMix, ParameterSmoothing.FastAlpha);
            int delayFrames = Math.Clamp((int)(_currentDelayMs * SampleRate / 1000f), 1, MaxFrames - 1);
            float mix = _currentMix;
            int index = frame * channels;
            float left = samples[index];
            float rightSample = samples[index + 1];

            int read = _write - delayFrames;
            if (read < 0) read += MaxFrames;
            float delayed = _buffer[read];

            _buffer[_write] = right ? rightSample : left;
            _write++;
            if (_write >= MaxFrames) _write = 0;

            if (right)
            {
                samples[index + 1] = rightSample + ((delayed - rightSample) * mix);
            }
            else
            {
                samples[index] = left + ((delayed - left) * mix);
            }
        }
    }

    public void Reset()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _write = 0;
        _primed = false;
    }
}
