namespace Ruse.App.Audio;

/// <summary>
/// Reversed-delay effect: a segment of the input is written backwards into a
/// buffer and played forward, producing a reversed echo.
/// </summary>
public sealed class ReverseDelayProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "reverse-delay";

    private const int SampleRate = 48000;
    private const int MaxChannels = 2;
    private const int MaxSeconds = 2;
    private const int MaxSamples = SampleRate * MaxSeconds;
    private const int FadeSamples = 256;

    private readonly AudioParameter[] _parameters;
    private readonly float[] _buffer = new float[MaxChannels * MaxSamples];

    private volatile float _timeMs = 500f;
    private volatile float _mix = 0.5f;
    private volatile float _level = 1f;

    private int _size = SampleRate / 2;
    private int _position;
    private int _pendingSize;
    private bool _primed;
    private bool _fadingOut;
    private bool _fadingIn;
    private float _fade = 1f;
    private int _fadeWait;
    private float _currentMix = 0.5f;
    private float _currentLevel = 1f;

    public ReverseDelayProcessor()
    {
        _parameters =
        [
            new AudioParameter
            {
                Name = "Time",
                Min = 100f,
                Max = 2000f,
                Default = 500f,
                Unit = "ms",
                Logarithmic = true,
                Get = () => _timeMs,
                Set = value => _timeMs = Math.Clamp(value, 100f, 2000f),
            },
            new AudioParameter
            {
                Name = "Mix",
                Min = 0f,
                Max = 100f,
                Default = 50f,
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
    public string DisplayName => "Reverse delay";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0)
        {
            return;
        }

        int size = Math.Clamp((int)MathF.Round(_timeMs * SampleRate / 1000f), 2, MaxSamples);
        if (!_primed)
        {
            Initialize(size);
        }
        else
        {
            HandleSizeChange(size);
        }

        _currentMix = ParameterSmoothing.Move(_currentMix, _mix, frames);
        _currentLevel = ParameterSmoothing.Move(_currentLevel, _level, frames);
        int channels = Math.Clamp(samples.Length / frames, 1, MaxChannels);
        double mix = _currentMix;
        double level = _currentLevel;
        float fadeStep = 1f / FadeSamples;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            int write = _size - 1 - _position;
            float fade = _fade;
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = channel * MaxSamples;
                float input = samples[index + channel];
                float wet = _buffer[offset + _position] * (float)level * fade;
                _buffer[offset + write] = input;
                samples[index + channel] = (float)Math.Clamp(
                    input + ((wet - input) * mix), -4.0, 4.0);
            }

            _position++;
            if (_position >= _size)
            {
                _position = 0;
            }

            AdvanceFade(fadeStep);
        }
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        _position = 0;
        _pendingSize = 0;
        _primed = false;
        _fadingOut = false;
        _fadingIn = false;
        _fade = 1f;
        _fadeWait = 0;
        _currentMix = _mix;
        _currentLevel = _level;
    }

    private void Initialize(int size)
    {
        _size = size;
        _position = 0;
        _primed = true;
        for (int channel = 0; channel < MaxChannels; channel++)
        {
            Array.Clear(_buffer, channel * MaxSamples, size);
        }

        // The read and write cursors cross halfway through the buffer. The wet
        // path is silent until that point, so hold the fade at zero and ramp it
        // in over the crossing instead of jumping from zero to the recorded tail.
        _fade = 0f;
        _fadeWait = Math.Max(0, size / 2);
        _fadingOut = false;
        _fadingIn = true;
    }

    private void HandleSizeChange(int size)
    {
        if (size == _size)
        {
            if (_fadingOut)
            {
                _fadingOut = false;
                _pendingSize = 0;
                _fadingIn = true;
            }

            return;
        }

        _pendingSize = size;
        if (!_fadingOut)
        {
            _fadingOut = true;
            _fadingIn = false;
        }
    }

    private void AdvanceFade(float step)
    {
        if (_fadingOut)
        {
            _fade -= step;
            if (_fade <= 0f)
            {
                int pending = _pendingSize;
                _pendingSize = 0;
                Initialize(pending);
            }
        }
        else if (_fadingIn)
        {
            if (_fadeWait > 0)
            {
                _fadeWait--;
                return;
            }

            _fade += step;
            if (_fade >= 1f)
            {
                _fade = 1f;
                _fadingIn = false;
            }
        }
    }
}
