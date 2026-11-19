using NAudio.Dsp;

namespace Ruse.App.Audio;

/// <summary>
/// Final output safety stage: a short look-ahead true-peak limiter followed by a
/// soft knee. The look-ahead lets gain reduce before a transient reaches the
/// output, so inter-sample peaks are contained without hard clipping. Runs on
/// the render thread and owns no allocations.
/// </summary>
public sealed class ClipGuard
{
    private const int MaxChannels = 2;
    private const int MaxLookaheadFrames = 256;
    private const float DefaultLookaheadMs = 1.3333334f;
    private const float DefaultCeiling = 0.98f;
    private const float DefaultAttackMs = 1.0f;
    private const float DefaultReleaseMs = 180.0f;

    private readonly float[] _delay = new float[MaxChannels * MaxLookaheadFrames];
    private Oversampler _oversamplerLeft = null!;
    private Oversampler _oversamplerRight = null!;

    private int _sampleRate;
    private float _attackMs = DefaultAttackMs;
    private float _releaseMs = DefaultReleaseMs;
    private volatile float _ceiling = DefaultCeiling;
    private volatile float _attack;
    private volatile float _release;
    private float _gain = 1f;
    private float _targetGain = 1f;
    private int _holdFrames;
    private int _delayPos;
    private int _lookaheadFrames;
    private bool _lookaheadExplicit;

    public ClipGuard()
    {
        ConfigureSampleRate(48000);
    }

    public ClipGuard(float ceilingLinear, float attackMs, float releaseMs)
    {
        ConfigureSampleRate(48000);
        Configure(ceilingLinear, attackMs, releaseMs);
    }

    public void ConfigureSampleRate(int sampleRate)
    {
        if (sampleRate <= 0 || sampleRate == _sampleRate)
        {
            return;
        }

        bool wasInitialized = _sampleRate != 0;
        _sampleRate = sampleRate;
        _oversamplerLeft = new Oversampler(4, sampleRate);
        _oversamplerRight = new Oversampler(4, sampleRate);
        if (!_lookaheadExplicit)
        {
            _lookaheadFrames = DefaultLookaheadFramesFor(sampleRate);
        }
        else
        {
            _lookaheadFrames = Math.Clamp(_lookaheadFrames, 1, MaxLookaheadFrames);
        }

        UpdateCoefficients();
        if (wasInitialized) Reset();
    }

    public void Configure(float ceilingLinear, float attackMs, float releaseMs)
    {
        _ceiling = Math.Clamp(ceilingLinear, 0.1f, 0.999f);
        _attackMs = Math.Clamp(attackMs, 0.1f, 1000f);
        _releaseMs = Math.Clamp(releaseMs, 1f, 10000f);
        UpdateCoefficients();
    }

    private void UpdateCoefficients()
    {
        int rate = Math.Max(1, _sampleRate);
        _attack = MathF.Exp(-1f / (_attackMs / 1000f * rate));
        _release = MathF.Exp(-1f / (_releaseMs / 1000f * rate));
    }

    private static int DefaultLookaheadFramesFor(int sampleRate) =>
        Math.Clamp((int)Math.Round(sampleRate * DefaultLookaheadMs / 1000.0),
            1, MaxLookaheadFrames);

    /// <summary>Look-ahead delay per channel, in frames.</summary>
    public int LatencySamples => _lookaheadFrames;

    public int LookaheadFrames => _lookaheadFrames;
    public double LatencyMilliseconds => _lookaheadFrames * 1000.0 / Math.Max(1, _sampleRate);

    public void SetLookahead(int frames)
    {
        _lookaheadExplicit = true;
        frames = Math.Clamp(frames, 1, MaxLookaheadFrames);
        if (frames == _lookaheadFrames) return;
        _lookaheadFrames = frames;
        Array.Clear(_delay);
        _delayPos = 0;
        _holdFrames = 0;
        _gain = 1f;
        _targetGain = 1f;
        Engaged = false;
    }

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

        Span<float> upsampled = stackalloc float[4];
        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            float peak = 0f;
            for (int channel = 0; channel < channels; channel++)
            {
                var oversampler = channel == 0 ? _oversamplerLeft : _oversamplerRight;
                oversampler.Upsample(samples[index + channel], upsampled);
                for (int i = 0; i < upsampled.Length; i++)
                {
                    float magnitude = MathF.Abs(upsampled[i]);
                    if (float.IsFinite(magnitude) && magnitude > peak)
                    {
                        peak = magnitude;
                    }
                }
            }

            float required = peak > ceiling ? ceiling / peak : 1f;
            if (required <= _targetGain)
            {
                _targetGain = required;
                _holdFrames = _lookaheadFrames;
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
                int offset = channel * MaxLookaheadFrames;
                float delayed = _delay[offset + delayedIndex];
                _delay[offset + delayedIndex] = samples[index + channel];
                samples[index + channel] = SoftLimit(delayed * _gain, ceiling);
            }

            _delayPos++;
            if (_delayPos >= _lookaheadFrames)
            {
                _delayPos = 0;
            }
        }
    }

    public void Reset()
    {
        Array.Clear(_delay);
        _oversamplerLeft?.Reset();
        _oversamplerRight?.Reset();
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
