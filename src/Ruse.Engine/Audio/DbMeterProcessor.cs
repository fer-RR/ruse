using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>Pass-through modifier that reports the RMS and peak level in dBFS.</summary>
public sealed class DbMeterProcessor : IAudioProcessor, IParameterizedProcessor, ITapProcessor
{
    public const string Id = "db-meter";
    public const string TapParameterName = "__tap";

    private static readonly ConcurrentDictionary<int, DbMeterTap> Taps = new();
    private static int _nextTapId = Random.Shared.Next(1, 1_000_000);
    private volatile float _tapId;
    private volatile bool _vertical = true;

    public DbMeterProcessor()
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
                Name = "Orientation",
                Min = 0,
                Max = 1,
                IsChoice = true,
                IsInteger = true,
                Options = new[] { "Horizontal", "Vertical" },
                Get = () => _vertical ? 1 : 0,
                Set = value => _vertical = value >= 0.5f,
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "dB meter";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int TapId => (int)MathF.Round(_tapId);
    public bool IsVertical
    {
        get => _vertical;
        set => _vertical = value;
    }

    public void SetTapId(int id)
    {
        _tapId = id;
        if (id > 0) Taps.GetOrAdd(id, _ => new DbMeterTap());
    }

    public void Process(Span<float> samples, int frames) => WriteTap(TapId, samples, frames);

    public static void WriteTap(int id, ReadOnlySpan<float> samples, int frames)
    {
        if (Taps.TryGetValue(id, out var tap)) tap.Write(samples, frames);
    }

    public void Reset() => Taps.TryRemove(TapId, out _);

    public static float GetDb(int id) => Taps.TryGetValue(id, out var tap) ? tap.Db : -120f;
    public static float GetPeakDb(int id) => Taps.TryGetValue(id, out var tap) ? tap.PeakDb : -120f;
    public static int NextTapId() => Interlocked.Increment(ref _nextTapId);
    public static void RemoveTap(int id) => Taps.TryRemove(id, out _);
}

internal sealed class DbMeterTap
{
    private const float MinimumDb = -100f;
    private readonly object _lock = new();
    private float _db = MinimumDb;
    private float _peakDb = MinimumDb;
    private long _peakTick;
    private long _lastWrite;

    public float Db { get { lock (_lock) return _db; } }
    public float PeakDb
    {
        get
        {
            lock (_lock)
            {
                DecayPeak(Environment.TickCount64);
                return _peakDb;
            }
        }
    }

    public void Write(ReadOnlySpan<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length == 0) return;
        double sum = 0;
        float peak = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float value = samples[i];
            sum += value * value;
            peak = Math.Max(peak, Math.Abs(value));
        }

        float rms = (float)Math.Sqrt(sum / samples.Length);
        float target = rms <= 0.000001f ? MinimumDb : 20f * MathF.Log10(rms);
        float peakDb = peak <= 0.000001f ? MinimumDb : 20f * MathF.Log10(peak);
        long now = Environment.TickCount64;
        lock (_lock)
        {
            float elapsed = _lastWrite == 0 ? 5f : Math.Clamp(now - _lastWrite, 1f, 100f);
            _lastWrite = now;
            target = Math.Clamp(target, MinimumDb, 6f);
            float time = target > _db ? 40f : 260f;
            float coefficient = 1f - MathF.Exp(-elapsed / time);
            _db += (target - _db) * coefficient;
            if (peakDb > _peakDb)
            {
                _peakDb = Math.Clamp(peakDb, MinimumDb, 6f);
                _peakTick = now;
            }
            else
            {
                DecayPeak(now);
            }
        }
    }

    private void DecayPeak(long now)
    {
        if (_peakTick == 0) return;
        long elapsed = now - _peakTick;
        if (elapsed < 900) return;
        _peakDb = Math.Max(_db, _peakDb - (elapsed - 900) * 0.018f);
        _peakTick = now;
    }
}
