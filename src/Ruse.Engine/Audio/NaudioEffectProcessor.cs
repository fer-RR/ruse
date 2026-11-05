using NAudio.Dsp;
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
    private readonly VibratoProcessor? _vibrato;
    private volatile bool _vibratoOn;

    private Func<float>? _rateGet;
    private Action<float>? _rateSet;
    private Action<double, NoteDivision>? _syncToTempo;
    private float _rateMin = 0.05f;
    private float _rateMax = 10f;
    private float _rateDefault = 1f;
    private volatile bool _tempoSync;
    private volatile float _tempo = 120f;
    private volatile int _division = 2;
    private volatile bool _syncDirty;

    public NaudioEffectProcessor(string typeId, string displayName, AudioEffect effect)
    {
        TypeId = typeId;
        DisplayName = displayName;
        _effect = effect;
        _effect.Configure(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels));

        if (_effect is PhaserEffect)
        {
            _vibrato = new VibratoProcessor();
            _vibrato.Parameters.First(parameter => parameter.Name == "Mix").Set(1f);
        }

        if (_effect is ChorusEffect chorus)
        {
            _rateGet = () => chorus.RateHz;
            _rateSet = value => chorus.RateHz = value;
            _syncToTempo = (bpm, division) => chorus.SyncToTempo(bpm, division);
        }
        else if (_effect is FlangerEffect flanger)
        {
            _rateGet = () => flanger.RateHz;
            _rateSet = value => flanger.RateHz = value;
            _syncToTempo = (bpm, division) => flanger.SyncToTempo(bpm, division);
        }
        else if (_effect is PhaserEffect phaser)
        {
            _rateGet = () => phaser.RateHz;
            _rateSet = value => phaser.RateHz = value;
            _syncToTempo = (bpm, division) => phaser.SyncToTempo(bpm, division);
        }
        else if (_effect is TremoloEffect tremolo)
        {
            _rateGet = () => tremolo.RateHz;
            _rateSet = value => tremolo.RateHz = value;
            _syncToTempo = (bpm, division) => tremolo.SyncToTempo(bpm, division);
        }

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

                // The modulation effects expose a RateHz property and a
                // SyncToTempo helper. Replace the generic Rate control with a
                // custom one so manual rate edits turn sync off.
                if (_rateGet != null && string.Equals(parameter.Name, "Rate", StringComparison.Ordinal))
                {
                    _rateMin = parameter.Minimum;
                    _rateMax = parameter.Maximum;
                    _rateDefault = parameter.DefaultValue;
                    mapped.Add(new AudioParameter
                    {
                        Name = "Rate",
                        Min = _rateMin,
                        Max = _rateMax,
                        Default = _rateDefault,
                        Unit = "Hz",
                        Logarithmic = true,
                        Get = () => _rateGet(),
                        Set = value =>
                        {
                            _rateSet?.Invoke(Math.Clamp(value, _rateMin, _rateMax));
                            _tempoSync = false;
                            _syncDirty = false;
                        },
                    });
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

        // Mix lives on AudioEffect and is deliberately excluded from the
        // generic parameter list, so expose it explicitly. Bypass is handled by
        // the effect node's enabled state; a second checkbox was redundant.
        mapped.Add(new AudioParameter
        {
            Name = "Mix",
            Min = 0f,
            Max = 1f,
            Default = _effect.Mix,
            Get = () => _effect.Mix,
            Set = v => _effect.Mix = v,
        });

        // The limiter's oversampling factor is a property, not a generic
        // parameter, so it needs an explicit control. True peak detection is
        // only meaningful when the detector can see inter-sample peaks.
        if (_effect is LimiterEffect limiter)
        {
            mapped.Add(new AudioParameter
            {
                Name = "Oversample",
                Min = 0f,
                Max = 2f,
                Default = limiter.OversampleFactor switch
                {
                    1 => 0f,
                    2 => 1f,
                    _ => 2f,
                },
                IsChoice = true,
                IsInteger = true,
                Options = new[] { "1x", "2x", "4x" },
                Get = () => limiter.OversampleFactor switch
                {
                    1 => 0f,
                    2 => 1f,
                    _ => 2f,
                },
                Set = v => limiter.OversampleFactor = v < 0.5f ? 1 : v < 1.5f ? 2 : 4,
            });
        }

        if (_rateGet != null)
        {
            mapped.Add(new AudioParameter
            {
                Name = "Tempo Sync",
                Min = 0f,
                Max = 1f,
                Default = 0f,
                IsToggle = true,
                Get = () => _tempoSync ? 1f : 0f,
                Set = value =>
                {
                    _tempoSync = value >= 0.5f;
                    _syncDirty = _tempoSync;
                },
            });

            mapped.Add(new AudioParameter
            {
                Name = "Tempo",
                Min = 40f,
                Max = 300f,
                Default = 120f,
                Unit = "BPM",
                Get = () => _tempo,
                Set = value =>
                {
                    _tempo = Math.Clamp(value, 40f, 300f);
                    _syncDirty = true;
                },
            });

            mapped.Add(new AudioParameter
            {
                Name = "Division",
                Min = 0f,
                Max = 11f,
                Default = 2f,
                IsChoice = true,
                IsInteger = true,
                Options = new[]
                {
                    "Whole", "Half", "Quarter", "Eighth", "Sixteenth", "1/32",
                    "Dotted Half", "Dotted Quarter", "Dotted Eighth",
                    "Triplet Quarter", "Triplet Eighth", "Triplet 16th",
                },
                Get = () => _division,
                Set = value =>
                {
                    _division = Math.Clamp((int)MathF.Round(value), 0, 11);
                    _syncDirty = true;
                },
            });
        }


        if (_vibrato != null)
        {
            mapped.Add(new AudioParameter
            {
                Name = "Vibrato",
                Min = 0f,
                Max = 1f,
                Default = 0f,
                IsToggle = true,
                Get = () => _vibratoOn ? 1f : 0f,
                Set = value => _vibratoOn = value >= 0.5f,
            });
            mapped.Add(VibratoParameter(_vibrato, "Rate", "Vibrato Rate"));
            mapped.Add(VibratoParameter(_vibrato, "Depth", "Vibrato Depth"));
        }

        _parameters = mapped.ToArray();
    }

    private static AudioParameter VibratoParameter(
        VibratoProcessor vibrato, string sourceName, string displayName)
    {
        var source = vibrato.Parameters.First(parameter => parameter.Name == sourceName);
        return new AudioParameter
        {
            Name = displayName,
            Min = source.Min,
            Max = source.Max,
            Default = source.Default,
            Unit = source.Unit,
            Get = () => source.Get(),
            Set = value => source.Set(value),
        };
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

    public void Process(Span<float> samples, int frames)
    {
        if (_tempoSync && _syncDirty && _syncToTempo != null)
        {
            _syncToTempo.Invoke(_tempo, ToNoteDivision(_division));
            _syncDirty = false;
        }

        if (_vibratoOn && _vibrato != null)
        {
            _vibrato.Process(samples, frames);
        }

        _effect.Process(samples);
    }

    public void Reset()
    {
        _vibrato?.Reset();
        _effect.Reset();
    }

    private static NoteDivision ToNoteDivision(int index) => index switch
    {
        0 => NoteDivision.Whole,
        1 => NoteDivision.Half,
        2 => NoteDivision.Quarter,
        3 => NoteDivision.Eighth,
        4 => NoteDivision.Sixteenth,
        5 => NoteDivision.ThirtySecond,
        6 => NoteDivision.DottedHalf,
        7 => NoteDivision.DottedQuarter,
        8 => NoteDivision.DottedEighth,
        9 => NoteDivision.TripletQuarter,
        10 => NoteDivision.TripletEighth,
        _ => NoteDivision.TripletSixteenth,
    };
}
