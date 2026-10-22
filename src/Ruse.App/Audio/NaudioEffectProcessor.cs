using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Adapts a NAudio <see cref="AudioEffect"/> (from NAudio.Effects, MIT) to the
/// modifier pipeline. The effect is configured once for the mixer's normalized
/// format (48 kHz, 32-bit float, stereo), and its own parameter list is mapped
/// onto <see cref="AudioParameter"/> so the generic modifier UI works unchanged.
/// </summary>
public sealed class NaudioEffectProcessor : IAudioProcessor, IParameterizedProcessor
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly AudioEffect _effect;
    private readonly AudioParameter[] _parameters;

    public NaudioEffectProcessor(string typeId, string displayName, AudioEffect effect)
    {
        TypeId = typeId;
        DisplayName = displayName;
        _effect = effect;
        _effect.Configure(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels));

        var mapped = new List<AudioParameter>();

        if (effect is IParameterized parameterized)
        {
            foreach (var parameter in parameterized.Parameters)
            {
                // Meters are display-only, so they are left out.
                if (parameter.Kind == EffectParameterKind.Meter)
                {
                    continue;
                }

                var source = parameter; // capture per iteration
                if (source.Kind == EffectParameterKind.Toggle)
                {
                    mapped.Add(new AudioParameter
                    {
                        Name = source.Name,
                        Min = 0f,
                        Max = 1f,
                        Default = source.DefaultValue,
                        Unit = source.Unit,
                        IsToggle = true,
                        Get = () => source.Value >= 0.5f ? 1f : 0f,
                        Set = v => source.Value = v >= 0.5f ? 1f : 0f,
                    });
                    continue;
                }

                if (source.Kind == EffectParameterKind.Choice)
                {
                    // A choice parameter carries the option names; the value is
                    // the index of the selected one.
                    var choices = source.Choices?.ToArray() ?? Array.Empty<string>();
                    mapped.Add(new AudioParameter
                    {
                        Name = source.Name,
                        Min = 0f,
                        Max = Math.Max(0, choices.Length - 1),
                        Default = source.DefaultValue,
                        IsChoice = true,
                        IsInteger = true,
                        Options = choices,
                        Get = () => source.Value,
                        Set = v => source.Value = Math.Clamp(v, 0f, Math.Max(0, choices.Length - 1)),
                    });
                    continue;
                }

                // Some NAudio parameters are backed by an integer property
                // (filter stages, for example). They are snapped so the value
                // the UI shows is the value the effect really holds.
                bool isInteger = IsIntegerBacked(source.Name);

                mapped.Add(new AudioParameter
                {
                    Name = source.Name,
                    Min = source.Minimum,
                    Max = source.Maximum,
                    Default = source.DefaultValue,
                    Unit = source.Unit,
                    Logarithmic = source.Unit == "Hz",
                    IsInteger = isInteger,
                    Get = () => source.Value,
                    Set = v => source.Value = isInteger ? MathF.Round(v) : v,
                });
            }
        }

        // Bypass/Mix live on AudioEffect and are deliberately excluded from the
        // generic parameter list, so expose them explicitly.
        mapped.Add(new AudioParameter
        {
            Name = "Mix",
            Min = 0f,
            Max = 1f,
            Default = 1f,
            Get = () => _effect.Mix,
            Set = v => _effect.Mix = v,
        });

        _parameters = mapped.ToArray();
    }

    public string TypeId { get; }
    public string DisplayName { get; }
    public IReadOnlyList<AudioParameter> Parameters => _parameters;

    /// <summary>
    /// True when the effect exposes a whole-number property with that name, so
    /// the parameter counts in steps instead of a continuous range.
    /// </summary>
    private bool IsIntegerBacked(string parameterName)
    {
        if (_integerBacked.TryGetValue(parameterName, out bool known))
        {
            return known;
        }

        bool result = false;
        var property = _effect.GetType().GetProperty(parameterName);
        if (property != null && property.CanRead && property.CanWrite)
        {
            Type type = property.PropertyType;
            result = type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte);
        }

        _integerBacked[parameterName] = result;
        return result;
    }

    private readonly Dictionary<string, bool> _integerBacked = new(StringComparer.Ordinal);

    /// <summary>Look-ahead/block latency introduced by the effect, in samples.</summary>
    public int LatencySamples => _effect.LatencySamples;

    public void Process(Span<float> samples, int frames) => _effect.Process(samples);

    public void Reset() => _effect.Reset();
}
