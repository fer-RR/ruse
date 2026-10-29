using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>Pass-through tap that keeps a short scrolling FFT history.</summary>
public sealed class SpectrogramProcessor : IAudioProcessor, IParameterizedProcessor, ITapProcessor
{
    public const string Id = "spectrogram";
    public const string TapParameterName = "__tap";
    public const int Columns = 96;

    private static readonly ConcurrentDictionary<int, SpectrogramTap> Taps = new();
    private static int _nextTapId = Random.Shared.Next(1, 1_000_000);

    private volatile float _tapId;
    private volatile float _contrast = 1f;
    private volatile float _floor = -80f;
    private volatile float _ceiling;

    public SpectrogramProcessor()
    {
        Parameters = new[]
        {
            new AudioParameter
            {
                Name = TapParameterName,
                Min = 0,
                Max = 16_000_000,
                IsInteger = true,
                Get = () => _tapId,
                Set = value => _tapId = Math.Clamp(MathF.Round(value), 0, 16_000_000),
            },
            new AudioParameter
            {
                Name = "Contrast",
                Min = 0.5f,
                Max = 2.5f,
                Get = () => _contrast,
                Set = value => _contrast = Math.Clamp(value, 0.5f, 2.5f),
                Unit = "x",
            },
            new AudioParameter
            {
                Name = "Floor",
                Min = -100f,
                Max = -30f,
                Get = () => _floor,
                Set = value => _floor = Math.Clamp(value, -100f, -30f),
                Unit = "dB",
            },
            new AudioParameter
            {
                Name = "Ceiling",
                Min = -40f,
                Max = 0f,
                Get = () => _ceiling,
                Set = value => _ceiling = Math.Clamp(value, -40f, 0f),
                Unit = "dB",
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Spectrogram";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int TapId => (int)MathF.Round(_tapId);
    public float Contrast => _contrast;
    public float Floor => _floor;
    public float Ceiling => _ceiling;

    public void SetTapId(int id)
    {
        _tapId = id;
        if (id > 0) Taps.GetOrAdd(id, _ => new SpectrogramTap());
    }

    public void Process(Span<float> samples, int frames) =>
        WriteTap(TapId, samples, frames, Contrast, Floor, Ceiling);

    public static void WriteTap(int id, ReadOnlySpan<float> samples, int frames,
        float contrast, float floor, float ceiling)
    {
        if (Taps.TryGetValue(id, out var tap))
        {
            tap.Write(samples, frames, contrast, floor, ceiling);
        }
    }

    public void Reset() => Taps.TryRemove(TapId, out _);

    public static int[]? GetColumns(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.Snapshot() : null;

    public static float GetPeakHz(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.PeakHz : 0f;

    public static int NextTapId() => Interlocked.Increment(ref _nextTapId);
    public static void RemoveTap(int id) => Taps.TryRemove(id, out _);
}

internal sealed class SpectrogramTap
{
    private readonly object _lock = new();
    private readonly FftAnalyzer _fft = new();
    private readonly int[] _columns = new int[SpectrogramProcessor.Columns * FftAnalyzer.Bands];
    private int _write;

    public float PeakHz
    {
        get { lock (_lock) return _fft.PeakHz; }
    }

    public void Write(ReadOnlySpan<float> samples, int frames,
        float contrast, float floor, float ceiling)
    {
        if (!_fft.Add(samples, frames)) return;
        float low = Math.Min(floor, ceiling - 1f);
        float high = Math.Max(ceiling, low + 1f);
        float curve = 1f / Math.Clamp(contrast, 0.5f, 2.5f);
        var levels = _fft.Levels;

        lock (_lock)
        {
            int offset = _write * FftAnalyzer.Bands;
            for (int band = 0; band < FftAnalyzer.Bands; band++)
            {
                float normalized = (Math.Clamp(levels[band], low, high) - low) / (high - low);
                int value = (int)MathF.Round(MathF.Pow(normalized, curve) * 255f);
                _columns[offset + band] = Math.Clamp(value, 0, 255);
            }

            _write = (_write + 1) % SpectrogramProcessor.Columns;
        }
    }

    public int[] Snapshot()
    {
        lock (_lock)
        {
            var result = new int[_columns.Length];
            int offset = 0;
            for (int column = 0; column < SpectrogramProcessor.Columns; column++)
            {
                int source = (_write + column) % SpectrogramProcessor.Columns;
                Array.Copy(_columns, source * FftAnalyzer.Bands, result, offset, FftAnalyzer.Bands);
                offset += FftAnalyzer.Bands;
            }

            return result;
        }
    }
}
