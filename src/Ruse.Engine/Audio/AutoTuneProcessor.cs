using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Scale-aware monophonic pitch correction. YIN finds the sung note and the
/// existing phase-vocoder pitch shifter applies the correction; a short FFT
/// keeps monitoring latency low.
/// </summary>
public sealed class AutoTuneProcessor : IAudioProcessor, IParameterizedProcessor, ISidechainProcessor
{
    public const string Id = "auto-tune";

    private static readonly string[] NoteNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static readonly string[] ScaleNames =
        ["Chromatic", "Major", "Minor", "Pentatonic", "Dorian", "Mixolydian", "Harmonic minor"];

    private static readonly int[][] Scales =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
        [0, 2, 4, 5, 7, 9, 11],
        [0, 2, 3, 5, 7, 8, 10],
        [0, 2, 4, 7, 9],
        [0, 2, 3, 5, 7, 9, 10],
        [0, 2, 4, 5, 7, 9, 10],
        [0, 2, 3, 5, 7, 8, 11],
    ];

    private readonly PitchShiftEffect _shift;
    private readonly NotePitchTracker _detector = new(highResolution: false);
    private readonly AudioParameter[] _parameters;
    private volatile float _key;
    private volatile float _scale;
    private volatile float _retuneMs = 40f;
    private volatile float _strength = 80f;
    private volatile float _mix = 100f;
    private volatile int _keyTapId;
    private float _correction;
    private float _target;

    public AutoTuneProcessor()
    {
        _shift = new PitchShiftEffect
        {
            FftSize = 1024,
            Oversampling = 4,
        };
        _shift.Configure(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        _shift.Mix = 1f;

        _parameters =
        [
            new AudioParameter
            {
                Name = "Key",
                Min = 0,
                Max = NoteNames.Length - 1,
                Default = 0,
                IsChoice = true,
                IsInteger = true,
                Options = NoteNames,
                Get = () => _key,
                Set = value => _key = Math.Clamp(MathF.Round(value), 0, NoteNames.Length - 1),
            },
            new AudioParameter
            {
                Name = "Scale",
                Min = 0,
                Max = ScaleNames.Length - 1,
                Default = 0,
                IsChoice = true,
                IsInteger = true,
                Options = ScaleNames,
                Get = () => _scale,
                Set = value => _scale = Math.Clamp(MathF.Round(value), 0, ScaleNames.Length - 1),
            },
            new AudioParameter
            {
                Name = "Retune",
                Min = 0f,
                Max = 200f,
                Default = 40f,
                Unit = "ms",
                Get = () => _retuneMs,
                Set = value => _retuneMs = Math.Clamp(value, 0f, 200f),
            },
            new AudioParameter
            {
                Name = "Strength",
                Min = 0f,
                Max = 100f,
                Default = 80f,
                Unit = "%",
                Get = () => _strength,
                Set = value => _strength = Math.Clamp(value, 0f, 100f),
            },
            new AudioParameter
            {
                Name = "Mix",
                Min = 0f,
                Max = 100f,
                Default = 100f,
                Unit = "%",
                Get = () => _mix,
                Set = value => _mix = Math.Clamp(value, 0f, 100f),
            },
        ];
    }

    public string TypeId => Id;
    public string DisplayName => "Auto-tune";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => _shift.LatencySamples;
    public void SetKeyTapId(int tapId) => _keyTapId = tapId;
    public void SetSidechain(ReadOnlySpan<float> samples, int frames) { }

    public void Process(Span<float> samples, int frames)
    {
        _detector.Add(samples, frames);
        _target = _detector.TryGetPitch(out float hz)
            ? TargetCorrection(hz)
            : 0f;

        float alpha = RetuneAlpha(frames);
        _correction += (_target - _correction) * alpha;
        _shift.PitchSemitones = _correction;
        _shift.Mix = _mix / 100f;
        _shift.Process(samples);
    }

    public void Reset()
    {
        _shift.Reset();
        _detector.Reset();
        _correction = 0f;
        _target = 0f;
    }

    private float TargetCorrection(float hz)
    {
        if (hz < 70f || hz > 1100f)
        {
            return 0f;
        }

        double midi = 69.0 + 12.0 * Math.Log2(hz / 440.0);
        if (_keyTapId > 0 && NoteDetectorProcessor.TryGetReading(_keyTapId, out var keyNote))
        {
            return Math.Clamp((float)(keyNote.Midi - midi) * (_strength / 100f), -12f, 12f);
        }

        int nearest = (int)Math.Round(midi);
        int[] scale = Scales[Math.Clamp((int)MathF.Round(_scale), 0, Scales.Length - 1)];
        int key = Math.Clamp((int)MathF.Round(_key), 0, 11);
        int best = nearest;
        double bestDistance = double.MaxValue;
        for (int offset = -6; offset <= 6; offset++)
        {
            int note = nearest + offset;
            int pitchClass = ((note - key) % 12 + 12) % 12;
            if (Array.IndexOf(scale, pitchClass) < 0)
            {
                continue;
            }

            double distance = Math.Abs(note - midi);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = note;
            }
        }

        float correction = (float)(best - midi) * (_strength / 100f);
        return Math.Clamp(correction, -2f, 2f);
    }

    private float RetuneAlpha(int frames)
    {
        if (_retuneMs <= 0f)
        {
            return 1f;
        }

        float samples = _retuneMs * 48f;
        return Math.Min(1f, frames / samples);
    }
}

/// <summary>Small allocation-free YIN detector for a monophonic vocal signal.</summary>
internal sealed class YinPitchDetector
{
    private const int SampleRate = 48000;
    private const int Window = 1024;
    private const int Hop = 256;
    private const int MinTau = 48;
    private const int MaxTau = 600;

    private readonly float[] _ring = new float[Window];
    private readonly float[] _window = new float[Window];
    private readonly float[] _difference = new float[MaxTau + 1];
    private readonly float[] _normalized = new float[MaxTau + 1];
    private int _write;
    private int _filled;
    private int _since;
    private float _pitch;
    private float _confidence;
    private bool _hasPitch;

    public void Add(ReadOnlySpan<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length < frames * 2)
        {
            return;
        }

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * 2;
            _ring[_write] = (samples[index] + samples[index + 1]) * 0.5f;
            _write = (_write + 1) % Window;
            if (_filled < Window)
            {
                _filled++;
            }

            _since++;
        }

        if (_filled < Window || _since < Hop)
        {
            return;
        }

        _since = 0;
        Detect();
    }

    public float Confidence => _confidence;
    public bool TryGetPitch(out float hz)
    {
        hz = _pitch;
        return _hasPitch;
    }

    public void Reset()
    {
        Array.Clear(_ring);
        _write = 0;
        _filled = 0;
        _since = 0;
        _pitch = 0f;
        _confidence = 0f;
        _hasPitch = false;
    }

    private void Detect()
    {
        double energy = 0;
        int start = _write;
        for (int i = 0; i < Window; i++)
        {
            float sample = _ring[(start + i) % Window];
            _window[i] = sample;
            energy += sample * sample;
        }

        if (energy / Window < 1e-7)
        {
            _confidence = 0f;
            _hasPitch = false;
            return;
        }

        for (int tau = MinTau; tau <= MaxTau; tau++)
        {
            double sum = 0;
            int limit = Window - tau;
            for (int i = 0; i < limit; i++)
            {
                double difference = _window[i] - _window[i + tau];
                sum += difference * difference;
            }

            _difference[tau] = (float)sum;
        }

        double running = 0;
        _normalized[0] = 1f;
        for (int tau = 1; tau <= MaxTau; tau++)
        {
            running += _difference[tau];
            _normalized[tau] = running <= 1e-12
                ? 1f
                : (float)(_difference[tau] * tau / running);
        }

        int bestTau = MinTau;
        float best = 1f;
        for (int tau = MinTau; tau <= MaxTau; tau++)
        {
            if (_normalized[tau] < best)
            {
                best = _normalized[tau];
                bestTau = tau;
                if (best < 0.1f)
                {
                    break;
                }
            }
        }

        if (best > 0.2f)
        {
            _confidence = 0f;
            _hasPitch = false;
            return;
        }

        double refined = bestTau;
        if (bestTau > MinTau && bestTau < MaxTau)
        {
            double previous = _normalized[bestTau - 1];
            double current = _normalized[bestTau];
            double next = _normalized[bestTau + 1];
            double denominator = 2.0 * (2.0 * current - previous - next);
            if (Math.Abs(denominator) > 1e-9)
            {
                refined += (previous - next) / denominator;
            }
        }

        if (refined <= 0)
        {
            _confidence = 0f;
            _hasPitch = false;
            return;
        }

        _pitch = (float)(SampleRate / refined);
        _confidence = Math.Clamp(1f - best, 0f, 1f);
        _hasPitch = _pitch >= 70f && _pitch <= 1100f;
    }
}
