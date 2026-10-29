using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Pitch shifter built on NAudio's Bernsee phase vocoder, with the FFT size
/// fixed per node so the route latency stays stable. The balanced variant uses
/// a 2048-point FFT (about 43 ms); the HQ variant keeps 4096 points (about 85 ms).
/// </summary>
public sealed class PitchShiftProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "pitch-shift";
    public const string HqId = "pitch-shift-hq";

    private readonly PitchShiftEffect _effect;
    private readonly AudioParameter[] _parameters;

    public PitchShiftProcessor(string typeId, string displayName, int fftSize)
    {
        TypeId = typeId;
        DisplayName = displayName;
        _effect = new PitchShiftEffect
        {
            FftSize = fftSize,
            Oversampling = 4,
        };
        _effect.Configure(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));

        _parameters = new[]
        {
            new AudioParameter
            {
                Name = "Pitch",
                Min = -12f,
                Max = 12f,
                Default = 0f,
                Unit = "st",
                Get = () => _effect.PitchSemitones,
                Set = value => _effect.PitchSemitones = Math.Clamp(value, -12f, 12f),
            },
            new AudioParameter
            {
                Name = "Oversampling",
                Min = 0f,
                Max = 2f,
                Default = 2f,
                IsChoice = true,
                IsInteger = true,
                Options = new[] { "1x", "2x", "4x" },
                Get = () => _effect.Oversampling switch
                {
                    1 => 0f,
                    2 => 1f,
                    _ => 2f,
                },
                Set = value =>
                {
                    _effect.Oversampling = value < 0.5f ? 1 : value < 1.5f ? 2 : 4;
                },
            },
            new AudioParameter
            {
                Name = "Mix",
                Min = 0f,
                Max = 1f,
                Default = 1f,
                Get = () => _effect.Mix,
                Set = value => _effect.Mix = Math.Clamp(value, 0f, 1f),
            },
        };
    }

    public string TypeId { get; }
    public string DisplayName { get; }
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => _effect.LatencySamples;

    public void Process(Span<float> samples, int frames) => _effect.Process(samples);

    public void Reset() => _effect.Reset();
}
