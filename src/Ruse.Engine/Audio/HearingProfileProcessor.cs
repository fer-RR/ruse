using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Per-ear hearing profile: independent left/right EQ and gain, a clean monitor
/// delay for lip-sync, optional loudness matching and a safety limiter.
/// </summary>
public sealed class HearingProfileProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "hearing-profile";

    private const int SampleRate = 48000;
    private const int BandCount = 8;
    private const int MaxDelayMs = 500;
    private const int MaxDelayFrames = SampleRate * MaxDelayMs / 1000;
    private const int LoudnessSegmentFrames = SampleRate / 10;
    private const float SilenceLufs = -120f;
    private const float MinLufs = -70f;

    private static readonly float[] BandFrequencies =
    {
        125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 12000f,
    };

    private readonly Equalizer _leftEqualizer;
    private readonly Equalizer _rightEqualizer;
    private readonly EqualizerBand[] _leftBands;
    private readonly EqualizerBand[] _rightBands;
    private readonly float[] _leftBandGains = new float[BandCount];
    private readonly float[] _rightBandGains = new float[BandCount];
    private readonly VariableDelayLine _delay = new(MaxDelayFrames, 2);
    private readonly ClipGuard _limiterGuard = new();

    private volatile float _leftGainDb;
    private volatile float _rightGainDb;
    private volatile float _delayMs;
    private volatile bool _loudnessMatch = true;
    private volatile float _targetLufs = -20f;
    private volatile float _maxBoostDb = 12f;
    private volatile bool _limiterEnabled = true;
    private volatile float _limiterCeilingDb = -1f;
    private volatile bool _eqDirty;

    private float[] _left = new float[8192];
    private float[] _right = new float[8192];
    private float[] _stereoScratch = new float[8192];

    private double _sumL;
    private double _sumR;
    private int _segmentFrames;
    private double _loudnessPower;
    private bool _loudnessPowerReady;
    private float _loudnessGainDb;
    private bool _limiterWasEnabled = true;

    // K-weighting state, per channel. Coefficients match BS.1770 at 48 kHz.
    private double _z1AL, _z2AL, _z1BL, _z2BL;
    private double _z1AR, _z2AR, _z1BR, _z2BR;

    public HearingProfileProcessor()
    {
        _leftBands = CreateBands();
        _rightBands = CreateBands();
        _leftEqualizer = new Equalizer(_leftBands);
        _rightEqualizer = new Equalizer(_rightBands);
        _leftEqualizer.Configure(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1));
        _rightEqualizer.Configure(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1));
        _leftEqualizer.Mix = 1f;
        _rightEqualizer.Mix = 1f;
        _leftEqualizer.Update();
        _rightEqualizer.Update();
        _limiterGuard.Configure(DbToLinear(_limiterCeilingDb), 1f, 180f);

        var parameters = new List<AudioParameter>
        {
            new()
            {
                Name = "Left gain",
                Min = -24f,
                Max = 24f,
                Default = 0f,
                Unit = "dB",
                Get = () => _leftGainDb,
                Set = value => _leftGainDb = Math.Clamp(value, -24f, 24f),
            },
            new()
            {
                Name = "Right gain",
                Min = -24f,
                Max = 24f,
                Default = 0f,
                Unit = "dB",
                Get = () => _rightGainDb,
                Set = value => _rightGainDb = Math.Clamp(value, -24f, 24f),
            },
        };

        for (int index = 0; index < BandCount; index++)
        {
            int band = index;
            string suffix = FormatFrequency(BandFrequencies[index]);
            parameters.Add(new AudioParameter
            {
                Name = $"Left {suffix}",
                Min = -24f,
                Max = 24f,
                Default = 0f,
                Unit = "dB",
                Get = () => _leftBandGains[band],
                Set = value =>
                {
                    _leftBandGains[band] = Math.Clamp(value, -24f, 24f);
                    _eqDirty = true;
                },
            });
        }

        for (int index = 0; index < BandCount; index++)
        {
            int band = index;
            string suffix = FormatFrequency(BandFrequencies[index]);
            parameters.Add(new AudioParameter
            {
                Name = $"Right {suffix}",
                Min = -24f,
                Max = 24f,
                Default = 0f,
                Unit = "dB",
                Get = () => _rightBandGains[band],
                Set = value =>
                {
                    _rightBandGains[band] = Math.Clamp(value, -24f, 24f);
                    _eqDirty = true;
                },
            });
        }

        parameters.Add(new AudioParameter
        {
            Name = "Monitor delay",
            Min = 0f,
            Max = MaxDelayMs,
            Default = 0f,
            Unit = "ms",
            IsInteger = true,
            Get = () => _delayMs,
            Set = value => _delayMs = Math.Clamp(MathF.Round(value), 0f, MaxDelayMs),
        });

        parameters.Add(new AudioParameter
        {
            Name = "Loudness match",
            Min = 0f,
            Max = 1f,
            Default = 1f,
            IsToggle = true,
            Get = () => _loudnessMatch ? 1f : 0f,
            Set = value => _loudnessMatch = value >= 0.5f,
        });

        parameters.Add(new AudioParameter
        {
            Name = "Target loudness",
            Min = -35f,
            Max = -10f,
            Default = -20f,
            Unit = "LUFS",
            IsInteger = true,
            Get = () => _targetLufs,
            Set = value => _targetLufs = Math.Clamp(value, -35f, -10f),
        });

        parameters.Add(new AudioParameter
        {
            Name = "Max boost",
            Min = 0f,
            Max = 24f,
            Default = 12f,
            Unit = "dB",
            IsInteger = true,
            Get = () => _maxBoostDb,
            Set = value => _maxBoostDb = Math.Clamp(value, 0f, 24f),
        });

        parameters.Add(new AudioParameter
        {
            Name = "Safety limiter",
            Min = 0f,
            Max = 1f,
            Default = 1f,
            IsToggle = true,
            Get = () => _limiterEnabled ? 1f : 0f,
            Set = value => _limiterEnabled = value >= 0.5f,
        });

        parameters.Add(new AudioParameter
        {
            Name = "Limiter ceiling",
            Min = -6f,
            Max = 0f,
            Default = -1f,
            Unit = "dB",
            Get = () => _limiterCeilingDb,
            Set = value =>
            {
                _limiterCeilingDb = Math.Clamp(value, -6f, 0f);
                _limiterGuard.Configure(DbToLinear(_limiterCeilingDb), 1f, 180f);
            },
        });

        Parameters = parameters.ToArray();
    }

    public string TypeId => Id;
    public string DisplayName => "Hearing profile";
    public IReadOnlyList<AudioParameter> Parameters { get; }

    // The monitor delay is an intentional output delay, not effect latency, so
    // route alignment must not try to compensate it.
    public int LatencySamples => _limiterGuard.LatencySamples;

    public void Process(Span<float> samples, int frames)
    {
        if (samples.IsEmpty || frames <= 0)
        {
            return;
        }

        int channels = Math.Max(1, samples.Length / frames);
        if (channels < 2)
        {
            int length = frames * 2;
            if (_stereoScratch.Length < length)
            {
                _stereoScratch = new float[Math.Max(length, _stereoScratch.Length * 2)];
            }

            for (int frame = 0; frame < frames; frame++)
            {
                float sample = samples[frame];
                _stereoScratch[frame * 2] = sample;
                _stereoScratch[frame * 2 + 1] = sample;
            }

            ProcessStereo(_stereoScratch.AsSpan(0, length), frames, 2);
            for (int frame = 0; frame < frames; frame++)
            {
                samples[frame] = (_stereoScratch[frame * 2] + _stereoScratch[frame * 2 + 1]) * 0.5f;
            }

            return;
        }

        ProcessStereo(samples, frames, channels);
    }

    private void ProcessStereo(Span<float> samples, int frames, int channels)
    {
        EnsureBuffers(frames);

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            _left[frame] = samples[index];
            _right[frame] = samples[index + 1];
        }

        if (_eqDirty)
        {
            ApplyEqualizerSettings();
            _eqDirty = false;
        }

        _leftEqualizer.Process(_left.AsSpan(0, frames));
        _rightEqualizer.Process(_right.AsSpan(0, frames));

        float leftGain = DbToLinear(_leftGainDb);
        float rightGain = DbToLinear(_rightGainDb);
        for (int frame = 0; frame < frames; frame++)
        {
            _left[frame] *= leftGain;
            _right[frame] *= rightGain;
        }

        MeasureLoudness(frames);
        float loudnessGain = UpdateLoudnessGain(frames);
        if (loudnessGain != 1f)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                _left[frame] *= loudnessGain;
                _right[frame] *= loudnessGain;
            }
        }

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            samples[index] = _left[frame];
            samples[index + 1] = _right[frame];
        }

        _delay.SetDelayFrames(DelayFrames());
        _delay.Process(samples, channels);

        if (_limiterEnabled)
        {
            _limiterGuard.Process(samples, frames);
            _limiterWasEnabled = true;
        }
        else if (_limiterWasEnabled)
        {
            _limiterGuard.Reset();
            _limiterWasEnabled = false;
        }
    }

    private void MeasureLoudness(int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            double weightedLeft = KWeight(_left[frame], ref _z1AL, ref _z2AL, ref _z1BL, ref _z2BL);
            double weightedRight = KWeight(_right[frame], ref _z1AR, ref _z2AR, ref _z1BR, ref _z2BR);
            _sumL += weightedLeft * weightedLeft;
            _sumR += weightedRight * weightedRight;
        }

        _segmentFrames += frames;
        if (_segmentFrames >= LoudnessSegmentFrames)
        {
            float power = (float)((_sumL + _sumR) / Math.Max(1, _segmentFrames));
            if (!_loudnessPowerReady)
            {
                _loudnessPower = power;
                _loudnessPowerReady = true;
            }
            else
            {
                // A 400 ms window keeps the level steady without pumping on syllables.
                _loudnessPower += (power - _loudnessPower) * 0.25;
            }

            _sumL = 0;
            _sumR = 0;
            _segmentFrames = 0;
        }

        _z1AL = DenormalMath.Flush(_z1AL);
        _z2AL = DenormalMath.Flush(_z2AL);
        _z1BL = DenormalMath.Flush(_z1BL);
        _z2BL = DenormalMath.Flush(_z2BL);
        _z1AR = DenormalMath.Flush(_z1AR);
        _z2AR = DenormalMath.Flush(_z2AR);
        _z1BR = DenormalMath.Flush(_z1BR);
        _z2BR = DenormalMath.Flush(_z2BR);
    }

    private float UpdateLoudnessGain(int frames)
    {
        float targetDb = 0f;
        if (_loudnessMatch && _loudnessPowerReady)
        {
            float lufs = ToLufs(_loudnessPower);
            if (lufs > MinLufs)
            {
                targetDb = Math.Clamp(_targetLufs - lufs, -12f, _maxBoostDb);
            }
            else
            {
                targetDb = _loudnessGainDb;
            }
        }

        float coefficient = 1f - MathF.Exp(-frames / (SampleRate * 0.25f));
        _loudnessGainDb += (targetDb - _loudnessGainDb) * coefficient;
        return DbToLinear(_loudnessGainDb);
    }

    private void ApplyEqualizerSettings()
    {
        for (int index = 0; index < BandCount; index++)
        {
            _leftBands[index].GainDb = _leftBandGains[index];
            _rightBands[index].GainDb = _rightBandGains[index];
        }

        _leftEqualizer.Update();
        _rightEqualizer.Update();
    }

    private int DelayFrames() =>
        (int)MathF.Round(Math.Clamp(_delayMs, 0f, MaxDelayMs) * SampleRate / 1000f);

    private void EnsureBuffers(int frames)
    {
        if (_left.Length >= frames)
        {
            return;
        }

        int size = Math.Max(frames, _left.Length * 2);
        _left = new float[size];
        _right = new float[size];
    }

    public void Reset()
    {
        _leftEqualizer.Reset();
        _rightEqualizer.Reset();
        _delay.Reset();
        _limiterGuard.Reset();
        Array.Clear(_left);
        Array.Clear(_right);
        Array.Clear(_stereoScratch);
        _sumL = 0;
        _sumR = 0;
        _segmentFrames = 0;
        _loudnessPower = 0;
        _loudnessPowerReady = false;
        _loudnessGainDb = 0;
        _limiterWasEnabled = true;
        _z1AL = _z2AL = _z1BL = _z2BL = 0;
        _z1AR = _z2AR = _z1BR = _z2BR = 0;
    }

    private static EqualizerBand[] CreateBands()
    {
        var bands = new EqualizerBand[BandCount];
        for (int index = 0; index < BandCount; index++)
        {
            bands[index] = new EqualizerBand
            {
                Type = EqualizerBandType.Peaking,
                Frequency = BandFrequencies[index],
                GainDb = 0f,
                Q = 1.0f,
            };
        }

        return bands;
    }

    private static string FormatFrequency(float frequency) =>
        frequency >= 1000f
            ? $"{frequency / 1000f:0.#} kHz"
            : $"{frequency:0} Hz";

    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);

    private static float ToLufs(double power) =>
        power <= 1e-12 ? SilenceLufs : -0.691f + (float)(10.0 * Math.Log10(power));

    private static double KWeight(
        float input,
        ref double z1A, ref double z2A,
        ref double z1B, ref double z2B)
    {
        double x = input;
        double y = (1.53512485958697 * x) + z1A;
        z1A = (-2.69169618940638 * x) - (-1.69065929318241 * y) + z2A;
        z2A = (1.19839281085285 * x) - (0.73248077421585 * y);

        double z = y;
        double output = z + z1B;
        z1B = (-2.0 * z) - (-1.99004745483398 * output) + z2B;
        z2B = z - (0.99007225036621 * output);
        return output;
    }
}
