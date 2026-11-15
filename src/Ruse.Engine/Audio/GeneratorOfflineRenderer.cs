namespace Ruse.App.Audio;

public sealed class GeneratorOfflineRenderer : IDisposable
{
    private readonly SourceNode _source;
    private readonly ToneOscillator? _oscillator;
    private readonly SynthGeneratorCapture? _synth;
    private readonly MetronomeCapture? _metronome;
    private long _position;

    public GeneratorOfflineRenderer(SourceNode source)
    {
        _source = source;
        if (source.IsMetronome)
        {
            _metronome = new MetronomeCapture(source.MetronomeBpm, source.MetronomeBeatsPerBar);
        }
        else if (source.Kind == SourceKind.Synth)
        {
            _synth = new SynthGeneratorCapture(source.Synth);
        }
        else
        {
            _oscillator = new ToneOscillator();
        }
    }

    public void Update(SourceNode source)
    {
        _synth?.Set(source.Synth);
        _metronome?.Set(source.MetronomeBpm, source.MetronomeBeatsPerBar);
    }

    public float[] Render(int frames)
    {
        frames = Math.Max(0, frames);
        if (_metronome != null)
        {
            var result = _metronome.RenderOffline(frames, _position);
            _position += frames;
            return result;
        }

        if (_synth != null)
        {
            var result = _synth.RenderOffline(frames);
            _position += frames;
            return result;
        }

        var tone = new float[frames * FormatConverter.TargetChannels];
        float gain = MathF.Pow(10f, _source.ToneLevelDb / 20f);
        for (int frame = 0; frame < frames; frame++)
        {
            float value = _oscillator!.Next(_source.ToneWave, _source.ToneFrequency) * gain;
            tone[frame * 2] = value;
            tone[(frame * 2) + 1] = value;
        }

        _position += frames;
        return tone;
    }

    public void Dispose()
    {
        _synth?.Dispose();
        _metronome?.Dispose();
    }
}
