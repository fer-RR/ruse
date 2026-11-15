namespace Ruse.App.Audio;

public static class GeneratorLoopRenderer
{
    public static float[] Render(SourceNode source, int frames)
    {
        frames = Math.Max(1, frames);
        if (source.IsMetronome)
        {
            var metronome = new MetronomeCapture(source.MetronomeBpm, source.MetronomeBeatsPerBar);
            return metronome.RenderOffline(frames);
        }

        if (source.Kind == SourceKind.Synth)
        {
            var synth = new SynthGeneratorCapture(source.Synth);
            return synth.RenderOffline(frames);
        }

        var oscillator = new ToneOscillator();
        var result = new float[frames * FormatConverter.TargetChannels];
        float gain = MathF.Pow(10f, source.ToneLevelDb / 20f);
        for (int frame = 0; frame < frames; frame++)
        {
            float value = oscillator.Next(source.ToneWave, source.ToneFrequency) * gain;
            result[frame * 2] = value;
            result[(frame * 2) + 1] = value;
        }

        return result;
    }
}
