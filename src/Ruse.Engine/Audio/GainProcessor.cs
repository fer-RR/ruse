namespace Ruse.App.Audio;

/// <summary>
/// A user-adjustable parameter exposed by an <see cref="IAudioProcessor"/> so a
/// generic UI can present a slider (or toggle) without knowing the effect type.
/// </summary>
public sealed class AudioParameter
{
    public required string Name { get; init; }
    public required float Min { get; init; }
    public required float Max { get; init; }
    public float Default { get; init; }

    /// <summary>Suffix shown after the value (e.g. "Hz"); empty for unitless.</summary>
    public string Unit { get; init; } = "";

    /// <summary>Use a logarithmic slider scale (natural for frequency).</summary>
    public bool Logarithmic { get; init; }

    /// <summary>Render as an on/off checkbox instead of a slider.</summary>
    public bool IsToggle { get; init; }

    /// <summary>
    /// The parameter counts in whole steps (e.g. filter stages). The UI snaps
    /// the slider to integers and the value is rounded before it is written.
    /// </summary>
    public bool IsInteger { get; init; }

    /// <summary>
    /// The parameter is a choice between named options. <see cref="Options"/>
    /// holds the names and the value is the index of the selected one.
    /// </summary>
    public bool IsChoice { get; init; }

    /// <summary>Option names for a choice parameter.</summary>
    public string[] Options { get; init; } = Array.Empty<string>();

    /// <summary>Reads the current value (safe from any thread).</summary>
    public required Func<float> Get { get; init; }

    /// <summary>Writes a new value (safe from any thread; read by the audio thread).</summary>
    public required Action<float> Set { get; init; }
}

/// <summary>
/// Linear gain applied to every sample. Gain of 1.0 is unity (pass-through).
/// </summary>
public sealed class GainProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "gain";

    // Written from the UI thread, read on the audio thread: volatile keeps the
    // float from being cached in a register across calls.
    private volatile float _gain = 1.0f;
    private volatile float _pan;
    private float _rampGain = 1.0f;
    private float _rampPan;
    private bool _rampInitialized;

    public string TypeId => Id;
    public string DisplayName => "Gain";

    public float Gain
    {
        get => _gain;
        set => _gain = value;
    }

    public float Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1f, 1f);
    }

    public IReadOnlyList<AudioParameter> Parameters { get; }

    public GainProcessor()
    {
        Parameters = new[]
        {
            new AudioParameter
            {
                Name = "Gain",
                Min = 0f,
                Max = 2f,
                Default = 1f,
                Get = () => _gain,
                Set = v => _gain = v,
            },
            new AudioParameter
            {
                Name = "Pan",
                Min = -1f,
                Max = 1f,
                Default = 0f,
                Get = () => _pan,
                Set = v => _pan = Math.Clamp(v, -1f, 1f),
            },
        };
    }

    public void Process(Span<float> samples, int frames)
    {
        float targetGain = _gain;
        float targetPan = _pan;
        if (!_rampInitialized)
        {
            _rampGain = targetGain;
            _rampPan = targetPan;
            _rampInitialized = true;
        }

        bool ramp = targetGain != _rampGain || targetPan != _rampPan;

        if (!ramp && targetGain == 1.0f && targetPan == 0f)
        {
            return;
        }

        int channels = frames > 0 ? Math.Max(1, samples.Length / frames) : 1;
        if (channels < 2)
        {
            if (!ramp)
            {
                for (int i = 0; i < samples.Length; i++) samples[i] *= targetGain;
            }
            else
            {
                float step = (targetGain - _rampGain) / Math.Max(1, frames);
                for (int frame = 0; frame < frames; frame++)
                {
                    _rampGain += step;
                    int index = frame * channels;
                    samples[index] *= _rampGain;
                }

                _rampGain = targetGain;
            }

            return;
        }

        if (ramp)
        {
            float gainStep = (targetGain - _rampGain) / Math.Max(1, frames);
            float panStep = (targetPan - _rampPan) / Math.Max(1, frames);
            for (int frame = 0; frame < frames; frame++)
            {
                _rampGain += gainStep;
                _rampPan += panStep;
                float left = _rampGain * Math.Min(1f, 1f - _rampPan);
                float right = _rampGain * Math.Min(1f, 1f + _rampPan);
                int index = frame * channels;
                samples[index] *= left;
                samples[index + 1] *= right;
            }

            _rampGain = targetGain;
            _rampPan = targetPan;
            return;
        }

        float leftGain = targetGain * Math.Min(1f, 1f - targetPan);
        float rightGain = targetGain * Math.Min(1f, 1f + targetPan);
        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * channels;
            samples[index] *= leftGain;
            samples[index + 1] *= rightGain;
        }
    }

    public void Reset()
    {
        _rampGain = _gain;
        _rampPan = _pan;
        _rampInitialized = false;
    }
}

/// <summary>
/// Implemented by processors that expose tweakable parameters to the UI.
/// </summary>
public interface IParameterizedProcessor
{
    IReadOnlyList<AudioParameter> Parameters { get; }
}
