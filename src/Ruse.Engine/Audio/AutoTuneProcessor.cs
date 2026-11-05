using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Scale-aware monophonic pitch correction. YIN finds the sung note and the
/// existing phase-vocoder pitch shifter applies the correction; a short FFT
/// keeps monitoring latency low.
/// </summary>
public sealed class AutoTuneProcessor : IAudioProcessor, IParameterizedProcessor, ISidechainProcessor, IDisposable
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
    private readonly PitchAnalysisWorker _pitchWorker;
    private readonly AudioParameter[] _parameters;
    private volatile float _key;
    private volatile float _scale;
    private volatile float _trackerMode;
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
        _pitchWorker = new PitchAnalysisWorker(
            highResolution: false, PitchTrackerMode.Autocorrelation);

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
            new AudioParameter
            {
                Name = "Tracker",
                Min = 0f,
                Max = 1f,
                Default = 0f,
                IsChoice = true,
                IsInteger = true,
                Options = ["Autocorrelation", "AMDF"],
                Get = () => _trackerMode,
                Set = value => SetTracker((int)MathF.Round(value)),
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
        _pitchWorker.Submit(samples, frames);
        _target = _pitchWorker.TryGetPitch(out float hz, out _)
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
        _pitchWorker.Reset();
        _correction = 0f;
        _target = 0f;
    }

    public void Dispose() => _pitchWorker.Dispose();

    private void SetTracker(int mode)
    {
        _trackerMode = Math.Clamp(mode, 0, 1);
        _pitchWorker.SetTracker((PitchTrackerMode)_trackerMode);
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
