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
                // Meters are display-only, and choice parameters need a combo box
                // rather than the generic slider UI, so leave them out for now.
                if (parameter.Kind is EffectParameterKind.Meter or EffectParameterKind.Choice)
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

                mapped.Add(new AudioParameter
                {
                    Name = source.Name,
                    Min = source.Minimum,
                    Max = source.Maximum,
                    Default = source.DefaultValue,
                    Unit = source.Unit,
                    Logarithmic = source.Unit == "Hz",
                    Get = () => source.Value,
                    Set = v => source.Value = v,
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

    /// <summary>Look-ahead/block latency introduced by the effect, in samples.</summary>
    public int LatencySamples => _effect.LatencySamples;

    public void Process(Span<float> samples, int frames) => _effect.Process(samples);

    public void Reset() => _effect.Reset();
}
