using Ruse.App.Audio;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private static List<AudioParameter> SourceAutomationParameters(SourceNode source)
    {
        var result = new List<AudioParameter>();
        if (source.IsMetronome)
        {
            result.Add(new AudioParameter
            {
                Name = "Tempo",
                Min = 20f,
                Max = 300f,
                Default = 120f,
                Unit = "BPM",
                IsInteger = true,
                Get = () => source.MetronomeBpm,
                Set = value => source.SetMetronome(value, source.MetronomeBeatsPerBar),
            });
            result.Add(new AudioParameter
            {
                Name = "Beats per bar",
                Min = 1f,
                Max = 16f,
                Default = 4f,
                IsInteger = true,
                Get = () => source.MetronomeBeatsPerBar,
                Set = value => source.SetMetronome(source.MetronomeBpm, (int)Math.Clamp(MathF.Round(value), 1f, 16f)),
            });
            return result;
        }

        if (source.Kind == SourceKind.Tone)
        {
            AddToneParameters(result, source);
            return result;
        }

        if (source.Kind != SourceKind.Synth)
        {
            return result;
        }

        result.Add(new AudioParameter
        {
            Name = "Frequency",
            Min = 20f,
            Max = 4000f,
            Default = 110f,
            Unit = "Hz",
            Logarithmic = true,
            Get = () => source.Synth.Frequency,
            Set = value => source.SetSynth(source.Synth with { Frequency = value }),
        });
        AddWaveformParameter(result, source);
        result.Add(new AudioParameter
        {
            Name = "Level",
            Min = -60f,
            Max = 0f,
            Default = -18f,
            Unit = "dB",
            Get = () => source.Synth.LevelDb,
            Set = value => source.SetSynth(source.Synth with { LevelDb = value }),
        });
        result.Add(new AudioParameter
        {
            Name = "Harmonics",
            Min = 0f,
            Max = 1f,
            Default = 0.3f,
            Get = () => source.Synth.Harmonics,
            Set = value => source.SetSynth(source.Synth with { Harmonics = value }),
        });
        result.Add(new AudioParameter
        {
            Name = "Detune",
            Min = 0f,
            Max = 50f,
            Default = 6f,
            Unit = "cents",
            Get = () => source.Synth.DetuneCents,
            Set = value => source.SetSynth(source.Synth with { DetuneCents = value }),
        });
        result.Add(new AudioParameter
        {
            Name = "Noise",
            Min = 0f,
            Max = 1f,
            Default = 0.05f,
            Get = () => source.Synth.Noise,
            Set = value => source.SetSynth(source.Synth with { Noise = value }),
        });
        result.Add(new AudioParameter
        {
            Name = "Cutoff",
            Min = 60f,
            Max = 14000f,
            Default = 2200f,
            Unit = "Hz",
            Logarithmic = true,
            Get = () => source.Synth.CutoffHz,
            Set = value => source.SetSynth(source.Synth with { CutoffHz = value }),
        });
        result.Add(new AudioParameter
        {
            Name = "Resonance",
            Min = 0f,
            Max = 1f,
            Default = 0.2f,
            Get = () => source.Synth.Resonance,
            Set = value => source.SetSynth(source.Synth with { Resonance = value }),
        });
        result.Add(new AudioParameter
        {
            Name = "Movement",
            Min = 0f,
            Max = 1f,
            Default = 0.15f,
            Get = () => source.Synth.Movement,
            Set = value => source.SetSynth(source.Synth with { Movement = value }),
        });
        result.Add(new AudioParameter
        {
            Name = "LFO rate",
            Min = 0.05f,
            Max = 12f,
            Default = 0.7f,
            Unit = "Hz",
            Logarithmic = true,
            Get = () => source.Synth.LfoRateHz,
            Set = value => source.SetSynth(source.Synth with { LfoRateHz = value }),
        });
        result.Add(new AudioParameter
        {
            Name = "Stereo width",
            Min = 0f,
            Max = 1f,
            Default = 0.4f,
            Get = () => source.Synth.StereoWidth,
            Set = value => source.SetSynth(source.Synth with { StereoWidth = value }),
        });
        return result;
    }

    private static void AddToneParameters(List<AudioParameter> result, SourceNode source)
    {
        result.Add(new AudioParameter
        {
            Name = "Frequency",
            Min = 20f,
            Max = 20000f,
            Default = 1000f,
            Unit = "Hz",
            Logarithmic = true,
            Get = () => source.ToneFrequency,
            Set = value => source.SetTone(value, source.ToneWave, source.ToneLevelDb),
        });
        AddWaveformParameter(result, source);
        result.Add(new AudioParameter
        {
            Name = "Level",
            Min = -60f,
            Max = 0f,
            Default = -18f,
            Unit = "dB",
            Get = () => source.ToneLevelDb,
            Set = value => source.SetTone(source.ToneFrequency, source.ToneWave, value),
        });
    }

    private static void AddWaveformParameter(List<AudioParameter> result, SourceNode source)
    {
        var waveforms = Enum.GetValues<ToneWaveform>();
        result.Add(new AudioParameter
        {
            Name = "Waveform",
            Min = 0,
            Max = waveforms.Length - 1,
            Default = 0,
            IsInteger = true,
            IsChoice = true,
            Options = waveforms.Select(wave => wave.ToString()).ToArray(),
            Get = () => Array.IndexOf(waveforms, source.Kind == SourceKind.Synth ? source.Synth.Waveform : source.ToneWave),
            Set = value =>
            {
                ToneWaveform wave = waveforms[Math.Clamp((int)MathF.Round(value), 0, waveforms.Length - 1)];
                if (source.Kind == SourceKind.Synth) source.SetSynth(source.Synth with { Waveform = wave });
                else source.SetTone(source.ToneFrequency, wave, source.ToneLevelDb);
            },
        });
    }

    private static List<ParameterDto> SourceParameterDtos(SourceNode source)
    {
        return SourceAutomationParameters(source).Select((parameter, index) => new ParameterDto
        {
            ProcessorIndex = -1,
            ParameterIndex = index,
            Name = parameter.Name,
            Min = parameter.Min,
            Max = parameter.Max,
            Value = parameter.Get(),
            Unit = parameter.Unit,
            Logarithmic = parameter.Logarithmic,
            IsToggle = parameter.IsToggle,
            IsInteger = parameter.IsInteger,
            IsChoice = parameter.IsChoice,
            Options = parameter.Options,
        }).ToList();
    }
}
