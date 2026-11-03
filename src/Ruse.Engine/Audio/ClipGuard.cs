namespace Ruse.App.Audio;

/// <summary>
/// Final output safety stage: a fast peak limiter followed by a soft knee, so
/// summed sources cannot hard-clip the device. Runs on the render thread and
/// owns no allocations.
/// </summary>
public sealed class ClipGuard
{
    private const int SampleRate = 48000;
    private const float DefaultCeiling = 0.98f;
    private const float DefaultAttackMs = 1.0f;
    private const float DefaultReleaseMs = 180.0f;

    private volatile float _ceiling = DefaultCeiling;
    private volatile float _attack = MathF.Exp(-1f / (DefaultAttackMs / 1000f * SampleRate));
    private volatile float _release = MathF.Exp(-1f / (DefaultReleaseMs / 1000f * SampleRate));
    private float _gain = 1f;

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

    public bool Engaged { get; private set; }
    public float GainReductionDb => 20f * MathF.Log10(Math.Max(0.0001f, _gain));

    public void Process(Span<float> samples, int frames)
    {
        if (samples.IsEmpty || frames <= 0)
        {
            return;
        }

        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float value = MathF.Abs(samples[i]);
            if (float.IsFinite(value) && value > peak)
            {
                peak = value;
            }
        }

        float ceiling = _ceiling;
        float target = peak > ceiling ? ceiling / peak : 1f;
        float coefficient = target < _gain ? _attack : _release;
        Engaged = target < 1f || _gain < 0.999f;

        for (int i = 0; i < samples.Length; i++)
        {
            _gain = target + (_gain - target) * coefficient;
            samples[i] = SoftLimit(samples[i] * _gain, ceiling);
        }
    }

    public void Reset()
    {
        _gain = 1f;
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
