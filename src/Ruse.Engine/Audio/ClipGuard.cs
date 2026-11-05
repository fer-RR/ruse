namespace Ruse.App.Audio;

/// <summary>
/// Final output safety stage: a short look-ahead true-peak limiter followed by a
/// soft knee. The look-ahead lets gain reduce before a transient reaches the
/// output, so inter-sample peaks are contained without hard clipping. Runs on
/// the render thread and owns no allocations.
/// </summary>
public sealed class ClipGuard
{
    private const int SampleRate = 48000;
    private const int MaxChannels = 2;
    private const int DefaultLookaheadFrames = 64;
    private const float DefaultCeiling = 0.98f;
    private const float DefaultAttackMs = 1.0f;
    private const float DefaultReleaseMs = 180.0f;

    private readonly float[] _delay = new float[MaxChannels * DefaultLookaheadFrames];
    private readonly float[] _previous = new float[MaxChannels];

    private volatile float _ceiling = DefaultCeiling;
    private volatile float _attack = MathF.Exp(-1f / (DefaultAttackMs / 1000f * SampleRate));
    private volatile float _release = MathF.Exp(-1f / (DefaultReleaseMs / 1000f * SampleRate));
    private float _gain = 1f;
    private float _targetGain = 1f;
    private int _holdFrames;
    private int _delayPos;

    public ClipGuard()
    {
    }

    public ClipGuard(float ceilingLinear, float attackMs, float releaseMs)
    {
        Configure(ceilingLinear, attackMs, releaseMs);
    }

    public void Configure(float ceilingLinear, float attackMs, float releaseMs)
    {
        _ceiling = Math.Clamp(ceilingLinear, 0.1f, 0.999f);
        _attack = MathF.Exp(-1f / (Math.Clamp(attackMs, 0.1f, 1000f) / 1000f * SampleRate));
        _release = MathF.Exp(-1f / (Math.Clamp(releaseMs, 1f, 10000f) / 1000f * SampleRate));
    }

    /// <summary>Look-ahead delay per channel, in frames.</summary>
    public int LatencySamples => DefaultLookaheadFrames;

    public bool Engaged { get; private set; }
    public float GainReductionDb => 20f * MathF.Log10(Math.Max(0.0001f, _gain));

    public void Process(Span<float> samples, int frames)
    {
        if (samples.IsEmpty || frames <= 0)
        {
            return;
        }

        int channels = Math.Clamp(samples.Length / frames, 1, MaxChannels);
        float ceiling = _ceiling;
        float attack = _attack;
        float release = _release;

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            float peak = 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                float current = samples[index + channel];
                float previous = _previous[channel];
                _previous[channel] = current;

                // Four-point interpolation catches most inter-sample peaks
                // without the cost of a full oversampled true-peak meter.
                float magnitude = MathF.Max(MathF.Abs(current), MathF.Abs(previous));
                magnitude = MathF.Max(magnitude, MathF.Abs(previous + ((current - previous) * 0.25f)));
                magnitude = MathF.Max(magnitude, MathF.Abs(previous + ((current - previous) * 0.5f)));
                magnitude = MathF.Max(magnitude, MathF.Abs(previous + ((current - previous) * 0.75f)));
                if (float.IsFinite(magnitude) && magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            float required = peak > ceiling ? ceiling / peak : 1f;
            if (required <= _targetGain)
            {
                _targetGain = required;
                _holdFrames = DefaultLookaheadFrames;
            }
            else if (_holdFrames > 0)
            {
                _holdFrames--;
            }
            else
            {
                _targetGain = 1f;
            }

            float coefficient = _targetGain < _gain ? attack : release;
            _gain = _targetGain + ((_gain - _targetGain) * coefficient);
            Engaged = _targetGain < 1f || _gain < 0.999f;

            int delayedIndex = _delayPos;
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = channel * DefaultLookaheadFrames;
                float delayed = _delay[offset + delayedIndex];
                _delay[offset + delayedIndex] = samples[index + channel];
                samples[index + channel] = SoftLimit(delayed * _gain, ceiling);
            }

            _delayPos++;
            if (_delayPos >= DefaultLookaheadFrames)
            {
                _delayPos = 0;
            }
        }
    }

    public void Reset()
    {
        Array.Clear(_delay);
        Array.Clear(_previous);
        _gain = 1f;
        _targetGain = 1f;
        _holdFrames = 0;
        _delayPos = 0;
        Engaged = false;
    }

    private static float SoftLimit(float sample, float ceiling)
    {
        if (!float.IsFinite(sample))
        {
            return 0f;
        }

        float magnitude = MathF.Abs(sample);
        if (magnitude <= ceiling)
        {
            return sample;
        }

        float over = (magnitude - ceiling) / (1f - ceiling);
        float limited = ceiling + (1f - ceiling) * (over / (1f + over));
        return MathF.CopySign(limited, sample);
    }
}
