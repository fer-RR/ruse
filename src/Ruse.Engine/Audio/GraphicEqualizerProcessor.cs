using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// NAudio's ten-band ISO octave graphic equalizer (MIT, already part of
/// NAudio.Core). Band gain changes are applied click-free by the effect.
/// </summary>
public sealed class GraphicEqualizerProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "graphic-eq";

    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const float MinGainDb = -12f;
    private const float MaxGainDb = 12f;

    private readonly GraphicEqualizer _equalizer;
    private readonly AudioParameter[] _parameters;

    public GraphicEqualizerProcessor()
    {
        _equalizer = new GraphicEqualizer(GraphicEqualizerLayout.TenBandOctave);
        _equalizer.Configure(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels));

        var parameters = new List<AudioParameter>(_equalizer.BandCount);
        for (int index = 0; index < _equalizer.BandCount; index++)
        {
            int band = index;
            string name = FormatFrequency(_equalizer.GetCentreFrequency(index));
            parameters.Add(new AudioParameter
            {
                Name = name,
                Min = MinGainDb,
                Max = MaxGainDb,
                Default = 0f,
                Unit = "dB",
                Get = () => _equalizer.GetBandGain(band),
                Set = value => _equalizer.SetBandGain(
                    band, Math.Clamp(value, MinGainDb, MaxGainDb)),
            });
        }

        _parameters = parameters.ToArray();
    }

    public string TypeId => Id;
    public string DisplayName => "Graphic EQ";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;

    public void Process(Span<float> samples, int frames) => _equalizer.Process(samples);

    public void Reset() => _equalizer.Reset();

    private static string FormatFrequency(float frequency) =>
        frequency >= 1000f
            ? $"{frequency / 1000f:0.#} kHz"
            : $"{frequency:0.#} Hz";
}
