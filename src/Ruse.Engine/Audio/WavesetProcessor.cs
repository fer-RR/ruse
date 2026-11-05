namespace Ruse.App.Audio;

/// <summary>
/// Records a short waveform segment up to the next zero crossing, then plays
/// it back and forth a few times before capturing another segment. This is a
/// clean-room take on the waveset idea rather than a copied Csound port.
/// </summary>
public sealed class WavesetProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "waveset";

    private const int SampleRate = 48000;
    private const int MaxChannels = 2;
    private const int MaxSamples = SampleRate;
    private const int FadeSamples = 256;

    private readonly AudioParameter[] _parameters;
    private readonly float[] _buffer = new float[MaxChannels * MaxSamples];

    private volatile float _lengthMs = 200f;
    private volatile float _repeats = 2f;
    private volatile float _mix = 0.5f;
    private volatile float _level = 1f;

    private int _size = SampleRate / 5;
    private int _pendingSize;
    private int _write;
    private int _segmentLength;
    private int _playPos;
    private int _playDirection = 1;
    private int _repeatsLeft = 2;
    private bool _recording = true;
    private float _lastInput;
    private bool _primed;
    private bool _fadingOut;
    private bool _fadingIn;
    private float _fade = 1f;
    private float _currentMix = 0.5f;
    private float _currentLevel = 1f;
    private float _transitionLeft;
    private float _transitionRight;
    private float _lastWetLeft;
    private float _lastWetRight;
    private int _transitionRemaining;

    public WavesetProcessor()
    {
        _parameters =
        [
            new AudioParameter
            {
                Name = "Length",
                Min = 10f,
                Max = 1000f,
                Default = 200f,
                Unit = "ms",
                Logarithmic = true,
                Get = () => _lengthMs,
                Set = value => _lengthMs = Math.Clamp(value, 10f, 1000f),
            },
            new AudioParameter
            {
                Name = "Repeats",
                Min = 1f,
                Max = 8f,
                Default = 2f,
                IsInteger = true,
                Get = () => _repeats,
                Set = value => _repeats = Math.Clamp(MathF.Round(value), 1f, 8f),
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
    public string DisplayName => "Waveset";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => 0;

    public void Process(Span<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0)
        {
            return;
        }

        int size = Math.Clamp((int)MathF.Round(_lengthMs * SampleRate / 1000f), 64, MaxSamples);
        if (!_primed)
        {
            Initialize(size);
        }
        else
        {
            HandleSizeChange(size);
        }

        int channels = Math.Clamp(samples.Length / frames, 1, MaxChannels);
        int repeatCount = Math.Clamp((int)MathF.Round(_repeats), 1, 8);
        int minimumSegment = Math.Max(32, _size / 8);
        _currentMix = ParameterSmoothing.Move(_currentMix, _mix, frames);
        _currentLevel = ParameterSmoothing.Move(_currentLevel, _level, frames);
        double mix = _currentMix;
        double level = _currentLevel;
        float fadeStep = 1f / FadeSamples;

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            float mono = 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                mono += samples[index + channel];
            }

            mono /= channels;
            bool crossing = _lastInput != 0f && mono != 0f && ((_lastInput > 0f) != (mono > 0f));
            bool wasRecording = _recording;
            int readPosition = _playPos;
            bool playing = !_recording;

            if (_recording)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    _buffer[(channel * MaxSamples) + _write] = samples[index + channel];
                }

                _write++;
                if (_write >= _size || (crossing && _write >= minimumSegment))
                {
                    _segmentLength = Math.Max(1, _write);
                    _playPos = _segmentLength - 1;
                    _playDirection = -1;
                    _repeatsLeft = repeatCount;
                    _recording = false;
                }
            }
            else
            {
                _playPos += _playDirection;
                if (_playPos < 0 || _playPos >= _segmentLength)
                {
                    if (_playPos < 0)
                    {
                        _playDirection = 1;
                        _playPos = 0;
                    }
                    else
                    {
                        _playDirection = -1;
                        _playPos = _segmentLength - 1;
                    }

                    _repeatsLeft--;
                    if (_repeatsLeft <= 0)
                    {
                        _recording = true;
                        _write = 0;
                        _playPos = 0;
                        _playDirection = 1;
                        _repeatsLeft = repeatCount;
                    }
                }
            }

            bool stateChanged = wasRecording != _recording;
            float fade = _fade;
            for (int channel = 0; channel < channels; channel++)
            {
                float input = samples[index + channel];
                float wet = playing && readPosition >= 0 && readPosition < _segmentLength
                    ? _buffer[(channel * MaxSamples) + readPosition] * (float)level * fade
                    : input;

                if (_transitionRemaining > 0)
                {
                    float t = 1f - (_transitionRemaining / (float)FadeSamples);
                    float from = channel == 0 ? _transitionLeft : _transitionRight;
                    wet = from + ((wet - from) * t);
                }

                if (channel == 0)
                {
                    _lastWetLeft = wet;
                }
                else
                {
                    _lastWetRight = wet;
                }

                samples[index + channel] = (float)Math.Clamp(
                    input + ((wet - input) * mix), -4.0, 4.0);
            }

            if (stateChanged)
            {
                _transitionLeft = _lastWetLeft;
                _transitionRight = _lastWetRight;
                _transitionRemaining = FadeSamples;
            }
            else if (_transitionRemaining > 0)
            {
                _transitionRemaining--;
            }

            _lastInput = mono;
            AdvanceFade(fadeStep);
        }
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        ResetState();
        _primed = false;
        _lastInput = 0f;
        _pendingSize = 0;
        _fadingOut = false;
        _fadingIn = false;
        _fade = 1f;
        _currentMix = _mix;
        _currentLevel = _level;
        _transitionRemaining = 0;
        _transitionLeft = 0f;
        _transitionRight = 0f;
        _lastWetLeft = 0f;
        _lastWetRight = 0f;
    }

    private void Initialize(int size)
    {
        _size = size;
        _lastInput = 0f;
        ResetState();
        for (int channel = 0; channel < MaxChannels; channel++)
        {
            Array.Clear(_buffer, channel * MaxSamples, size);
        }

        _primed = true;
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
                _fade = 0f;
                Initialize(_pendingSize);
                _pendingSize = 0;
                _fadingOut = false;
                _fadingIn = true;
            }
        }
        else if (_fadingIn)
        {
            _fade += step;
            if (_fade >= 1f)
            {
                _fade = 1f;
                _fadingIn = false;
            }
        }
    }

    private void ResetState()
    {
        _write = 0;
        _segmentLength = 0;
        _playPos = 0;
        _playDirection = 1;
        _repeatsLeft = Math.Clamp((int)MathF.Round(_repeats), 1, 8);
        _recording = true;
        _transitionRemaining = 0;
        _lastWetLeft = 0f;
        _lastWetRight = 0f;
    }
}
