using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Three-band dynamics with independent threshold, ratio, timing and make-up
/// for each band. The NAudio effect does not expose its bands through the
/// generic parameter model, so this wrapper maps them onto normal node sliders.
/// </summary>
public sealed class MultibandCompressorProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "multiband-compressor";

    private readonly MultibandCompressorEffect _effect;
    private readonly AudioParameter[] _parameters;

    public MultibandCompressorProcessor()
    {
        _effect = new MultibandCompressorEffect(new[] { 200f, 2000f });
        _effect.Configure(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));

        var parameters = new List<AudioParameter>();
        string[] bandNames = { "Low", "Mid", "High" };
        for (int i = 0; i < _effect.Bands.Count; i++)
        {
            var band = _effect.Bands[i];
            string name = i < bandNames.Length ? bandNames[i] : $"Band {i + 1}";
            parameters.Add(new AudioParameter
            {
                Name = $"{name} Threshold",
                Min = -60f,
                Max = 0f,
                Default = band.ThresholdDb,
                Unit = "dB",
                Get = () => band.ThresholdDb,
                Set = value => band.ThresholdDb = Math.Clamp(value, -60f, 0f),
            });
            parameters.Add(new AudioParameter
            {
                Name = $"{name} Ratio",
                Min = 1f,
                Max = 20f,
                Default = band.Ratio,
                Get = () => band.Ratio,
                Set = value => band.Ratio = Math.Clamp(value, 1f, 20f),
            });
            parameters.Add(new AudioParameter
            {
                Name = $"{name} Attack",
                Min = 0.1f,
                Max = 200f,
                Default = band.AttackMs,
                Unit = "ms",
                Get = () => band.AttackMs,
                Set = value => band.AttackMs = Math.Clamp(value, 0.1f, 200f),
            });
            parameters.Add(new AudioParameter
            {
                Name = $"{name} Release",
                Min = 5f,
                Max = 1000f,
                Default = band.ReleaseMs,
                Unit = "ms",
                Get = () => band.ReleaseMs,
                Set = value => band.ReleaseMs = Math.Clamp(value, 5f, 1000f),
            });
            parameters.Add(new AudioParameter
            {
                Name = $"{name} Make-up",
                Min = 0f,
                Max = 24f,
                Default = band.MakeUpGainDb,
                Unit = "dB",
                Get = () => band.MakeUpGainDb,
                Set = value => band.MakeUpGainDb = Math.Clamp(value, 0f, 24f),
            });
        }

        parameters.Add(new AudioParameter
        {
            Name = "Mix",
            Min = 0f,
            Max = 1f,
            Default = 1f,
            Get = () => _effect.Mix,
            Set = value => _effect.Mix = Math.Clamp(value, 0f, 1f),
        });

        _parameters = parameters.ToArray();
    }

    public string TypeId => Id;
    public string DisplayName => "Multiband compressor";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => _effect.LatencySamples;

    public void Process(Span<float> samples, int frames) => _effect.Process(samples);

    public void Reset() => _effect.Reset();
}
