using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Hard clipper with a drive control and an output ceiling. The underlying
/// NAudio saturation stage oversamples 4x, so the harmonics it creates do not
/// fold back into the audible band as badly as a naive per-sample clamp.
/// </summary>
public sealed class ClipperProcessor : IAudioProcessor, IParameterizedProcessor, INativeProcessor
{
    public const string Id = "clipper";

    private const float MinDriveDb = 0f;
    private const float MaxDriveDb = 36f;
    private const float MinCeilingDb = -24f;
    private const float MaxCeilingDb = 0f;

    private readonly SaturationEffect _effect;
    private readonly AudioParameter[] _parameters;
    private float[] _wet = new float[8192];

    private volatile float _driveDb;
    private volatile float _ceilingDb = -0.3f;
    private volatile float _mix = 1f;
    private float _currentDrive = 1f;
    private float _currentCeiling = 0.9660509f;
    private float _currentMix = 1f;
    private bool _primed;

    public ClipperProcessor()
    {
        _effect = new SaturationEffect
        {
            Curve = SaturationCurve.HardClip,
            OversampleFactor = 4,
            DriveDb = 0f,
            OutputGainDb = 0f,
            Mix = 1f,
        };
        _effect.Configure(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));

        _parameters = new[]
        {
            new AudioParameter
            {
                Name = "Drive",
                Min = MinDriveDb,
                Max = MaxDriveDb,
                Default = 0f,
                Unit = "dB",
                Get = () => _driveDb,
                Set = value => _driveDb = Math.Clamp(value, MinDriveDb, MaxDriveDb),
            },
            new AudioParameter
            {
                Name = "Ceiling",
                Min = MinCeilingDb,
                Max = MaxCeilingDb,
                Default = -0.3f,
                Unit = "dB",
                Get = () => _ceilingDb,
                Set = value => _ceilingDb = Math.Clamp(value, MinCeilingDb, MaxCeilingDb),
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
    public string DisplayName => "Clipper";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => _effect.LatencySamples;

    public void Process(Span<float> samples, int frames)
    {
        if (samples.IsEmpty || frames <= 0)
        {
            return;
        }

        float targetDrive = MathF.Pow(10f, _driveDb / 20f);
        float targetCeiling = MathF.Pow(10f, _ceilingDb / 20f);
        float targetMix = _mix;
        if (!_primed)
        {
            _currentDrive = targetDrive;
            _currentCeiling = targetCeiling;
            _currentMix = targetMix;
            _primed = true;
        }

        if (_wet.Length < samples.Length)
        {
            _wet = new float[Math.Max(samples.Length, _wet.Length * 2)];
        }

        // Ramp the drive across the block so moving the control cannot click.
        float driveStep = (targetDrive - _currentDrive) / samples.Length;
        float ceilingStep = (targetCeiling - _currentCeiling) / samples.Length;
        float mixStep = (targetMix - _currentMix) / samples.Length;
        float drive = _currentDrive;
        float ceiling = _currentCeiling;
        float mix = _currentMix;

        for (int i = 0; i < samples.Length; i++)
        {
            _wet[i] = samples[i] * drive;
            drive += driveStep;
        }

        _effect.Process(_wet.AsSpan(0, samples.Length));

        for (int i = 0; i < samples.Length; i++)
        {
            float dry = samples[i];
            samples[i] = dry + (((_wet[i] * ceiling) - dry) * mix);
            ceiling += ceilingStep;
            mix += mixStep;
        }

        _currentDrive = targetDrive;
        _currentCeiling = targetCeiling;
        _currentMix = targetMix;
    }

    public void Reset()
    {
        _primed = false;
        _effect.Reset();
    }
}
