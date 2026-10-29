using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>Pass-through tap that exposes a smoothed log-frequency spectrum.</summary>
public sealed class SpectrumProcessor : IAudioProcessor, IParameterizedProcessor, ITapProcessor
{
    public const string Id = "spectrum";
    public const string TapParameterName = "__tap";

    private static readonly ConcurrentDictionary<int, SpectrumTap> Taps = new();
    private static int _nextTapId = Random.Shared.Next(1, 1_000_000);

    private volatile float _tapId;
    private volatile float _smoothing = 55f;
    private volatile float _floor = -80f;
    private volatile float _ceiling;

    public SpectrumProcessor()
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
                Name = "Smoothing",
                Min = 0f,
                Max = 95f,
                Get = () => _smoothing,
                Set = value => _smoothing = Math.Clamp(value, 0f, 95f),
                Unit = "%",
            },
            new AudioParameter
            {
                Name = "Floor",
                Min = -100f,
                Max = -20f,
                Get = () => _floor,
                Set = value => _floor = Math.Clamp(value, -100f, -20f),
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
    public string DisplayName => "Spectrum";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int TapId => (int)MathF.Round(_tapId);
    public float Smoothing => _smoothing;
    public float Floor => _floor;
    public float Ceiling => _ceiling;

    public void SetTapId(int id)
    {
        _tapId = id;
        if (id > 0) Taps.GetOrAdd(id, _ => new SpectrumTap());
    }

    public void Process(Span<float> samples, int frames) =>
        WriteTap(TapId, samples, frames, Smoothing, Floor, Ceiling);

    public static void WriteTap(int id, ReadOnlySpan<float> samples, int frames,
        float smoothing, float floor, float ceiling)
    {
        if (Taps.TryGetValue(id, out var tap))
        {
            tap.Write(samples, frames, smoothing, floor, ceiling);
        }
    }

    public void Reset() => Taps.TryRemove(TapId, out _);

    public static float[]? GetBands(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.Snapshot() : null;

    public static float GetPeakHz(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.PeakHz : 0f;

    public static int NextTapId() => Interlocked.Increment(ref _nextTapId);
    public static void RemoveTap(int id) => Taps.TryRemove(id, out _);
}

internal sealed class SpectrumTap
{
    private readonly object _lock = new();
    private readonly FftAnalyzer _fft = new();
    private readonly float[] _bands = Enumerable.Repeat(-100f, FftAnalyzer.Bands).ToArray();

    public float PeakHz
    {
        get { lock (_lock) return _fft.PeakHz; }
    }

    public void Write(ReadOnlySpan<float> samples, int frames,
        float smoothing, float floor, float ceiling)
    {
        if (!_fft.Add(samples, frames)) return;
        float low = Math.Min(floor, ceiling - 1f);
        float high = Math.Max(ceiling, low + 1f);
        float release = 1f - Math.Clamp(smoothing, 0f, 95f) / 100f;
        var levels = _fft.Levels;

        lock (_lock)
        {
            for (int i = 0; i < _bands.Length; i++)
            {
                float value = Math.Clamp(levels[i], low, high);
                float factor = value > _bands[i] ? 0.72f : release;
                _bands[i] += (value - _bands[i]) * factor;
            }
        }
    }

    public float[] Snapshot()
    {
        lock (_lock) return (float[])_bands.Clone();
    }
}
