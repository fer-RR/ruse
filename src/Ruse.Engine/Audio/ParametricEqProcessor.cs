using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Six-band parametric EQ: a low shelf, four peaking bands and a high shelf.
/// The bands live in one NAudio equalizer so the response is retuned with a
/// click-free crossfade instead of rebuilding a chain of separate nodes.
/// </summary>
public sealed class ParametricEqProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "parametric-eq";
    public const int BandCount = 6;

    /// <summary>Band shapes in display order, used by the curve UI.</summary>
    public static readonly string[] BandTypes =
    {
        "Low shelf", "Peak", "Peak", "Peak", "Peak", "High shelf",
    };

    private static readonly float[] DefaultFrequencies = { 80f, 250f, 1000f, 3000f, 8000f, 12000f };
    private static readonly float[] DefaultQ = { 0.7f, 0.7f, 0.7f, 0.7f, 0.7f, 0.7f };

    private readonly Equalizer _equalizer;
    private readonly EqualizerBand[] _bands;
    private readonly AudioParameter[] _parameters;

    private volatile bool _dirty;
    private volatile float _mix = 1f;

    public ParametricEqProcessor()
    {
        _bands = new EqualizerBand[BandCount];
        for (int index = 0; index < BandCount; index++)
        {
            bool lowShelf = index == 0;
            bool highShelf = index == BandCount - 1;
            _bands[index] = new EqualizerBand
            {
                Type = lowShelf ? EqualizerBandType.LowShelf
                    : highShelf ? EqualizerBandType.HighShelf
                    : EqualizerBandType.Peaking,
                Frequency = DefaultFrequencies[index],
                GainDb = 0f,
                Q = DefaultQ[index],
                ShelfSlope = 1f,
            };
        }

        _equalizer = new Equalizer(_bands);
        _equalizer.Configure(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        _equalizer.Update();

        var parameters = new List<AudioParameter>();
        for (int index = 0; index < BandCount; index++)
        {
            int band = index;
            string prefix = $"Band {index + 1}";
            bool peaking = _bands[index].Type == EqualizerBandType.Peaking;

            parameters.Add(new AudioParameter
            {
                Name = $"{prefix} Frequency",
                Min = 20f,
                Max = 20000f,
                Default = DefaultFrequencies[index],
                Unit = "Hz",
                Logarithmic = true,
                Get = () => _bands[band].Frequency,
                Set = value =>
                {
                    _bands[band].Frequency = Math.Clamp(value, 20f, 20000f);
                    _dirty = true;
                },
            });

            parameters.Add(new AudioParameter
            {
                Name = $"{prefix} Gain",
                Min = -24f,
                Max = 24f,
                Default = 0f,
                Unit = "dB",
                Get = () => _bands[band].GainDb,
                Set = value =>
                {
                    _bands[band].GainDb = Math.Clamp(value, -24f, 24f);
                    _dirty = true;
                },
            });

            if (peaking)
            {
                parameters.Add(new AudioParameter
                {
                    Name = $"{prefix} Q",
                    Min = 0.1f,
                    Max = 10f,
                    Default = DefaultQ[index],
                    Get = () => _bands[band].Q,
                    Set = value =>
                    {
                        _bands[band].Q = Math.Clamp(value, 0.1f, 10f);
                        _dirty = true;
                    },
                });
            }
        }

        parameters.Add(new AudioParameter
        {
            Name = "Mix",
            Min = 0f,
            Max = 1f,
            Default = 1f,
            Get = () => _mix,
            Set = value => _mix = Math.Clamp(value, 0f, 1f),
        });

        _parameters = parameters.ToArray();
    }

    public string TypeId => Id;
    public string DisplayName => "Parametric EQ";
    public IReadOnlyList<AudioParameter> Parameters => _parameters;
    public int LatencySamples => _equalizer.LatencySamples;

    public void Process(Span<float> samples, int frames)
    {
        if (_dirty)
        {
            _equalizer.Update();
            _dirty = false;
        }

        _equalizer.Mix = _mix;
        _equalizer.Process(samples);
    }

    public void Reset() => _equalizer.Reset();
}
