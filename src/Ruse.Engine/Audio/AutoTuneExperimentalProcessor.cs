namespace Ruse.App.Audio;

/// <summary>
/// Experimental vocal tuner using TD-PSOLA grains. Unlike the phase-vocoder
/// Auto-tune node, grains are copied at their original rate so formants are
/// preserved; the tradeoff is a more experimental, slightly higher-latency path.
/// </summary>
public sealed class AutoTuneExperimentalProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "auto-tune-experimental";

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

    private readonly PsolaPitchShifter _shifter = new();
    private readonly YinPitchDetector _detector = new();
    private readonly AudioParameter[] _parameters;
    private volatile float _key;
    private volatile float _scale;
    private volatile float _retuneMs = 40f;
    private volatile float _strength = 80f;
    private volatile float _mix = 100f;
    private float _correction;

    public AutoTuneExperimentalProcessor()
    {
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
    public string DisplayName => "Auto-tune (experimental)";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => _shifter.LatencySamples;

    public void Process(Span<float> samples, int frames)
    {
        _detector.Add(samples, frames);
        bool voiced = _detector.TryGetPitch(out float hz);
        float target = voiced ? TargetCorrection(hz) : 0f;
        float alpha = RetuneAlpha(frames);
        _correction += (target - _correction) * alpha;
        _shifter.Process(samples, frames, voiced ? hz : 0f, _correction, _mix / 100f);
    }

    public void Reset()
    {
        _shifter.Reset();
        _detector.Reset();
        _correction = 0f;
    }

    private float TargetCorrection(float hz)
    {
        if (hz < 70f || hz > 1100f)
        {
            return 0f;
        }

        double midi = 69.0 + 12.0 * Math.Log2(hz / 440.0);
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

        return Math.Clamp((float)(best - midi) * (_strength / 100f), -2f, 2f);
    }

    private float RetuneAlpha(int frames)
    {
        if (_retuneMs <= 0f)
        {
            return 1f;
        }

        return Math.Min(1f, frames / (_retuneMs * 48f));
    }
}
