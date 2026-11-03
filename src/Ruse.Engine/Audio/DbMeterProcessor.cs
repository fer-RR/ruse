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
                Default = 1,
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
    public static float GetDbLeft(int id) => Taps.TryGetValue(id, out var tap) ? tap.DbLeft : -120f;
    public static float GetDbRight(int id) => Taps.TryGetValue(id, out var tap) ? tap.DbRight : -120f;
    public static float GetPeakDbLeft(int id) => Taps.TryGetValue(id, out var tap) ? tap.PeakDbLeft : -120f;
    public static float GetPeakDbRight(int id) => Taps.TryGetValue(id, out var tap) ? tap.PeakDbRight : -120f;
    public static int NextTapId() => Interlocked.Increment(ref _nextTapId);
    public static void RemoveTap(int id) => Taps.TryRemove(id, out _);
}

internal sealed class DbMeterTap
{
    private const float MinimumDb = -100f;
    private readonly object _lock = new();
    private float _dbLeft = MinimumDb;
    private float _dbRight = MinimumDb;
    private float _peakDbLeft = MinimumDb;
    private float _peakDbRight = MinimumDb;
    private long _peakTickLeft;
    private long _peakTickRight;
    private long _lastWrite;

    public float Db { get { lock (_lock) return Math.Max(_dbLeft, _dbRight); } }
    public float DbLeft { get { lock (_lock) return _dbLeft; } }
    public float DbRight { get { lock (_lock) return _dbRight; } }
    public float PeakDb
    {
        get
        {
            lock (_lock)
            {
                long now = Environment.TickCount64;
                DecayPeak(ref _peakDbLeft, ref _peakTickLeft, now);
                DecayPeak(ref _peakDbRight, ref _peakTickRight, now);
                return Math.Max(_peakDbLeft, _peakDbRight);
            }
        }
    }

    public float PeakDbLeft { get { lock (_lock) return _peakDbLeft; } }
    public float PeakDbRight { get { lock (_lock) return _peakDbRight; } }

    public void Write(ReadOnlySpan<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length < 2) return;
        int sampleCount = Math.Min(samples.Length, frames * 2);
        int channelFrames = sampleCount / 2;
        if (channelFrames <= 0) return;

        double sumLeft = 0;
        double sumRight = 0;
        float peakLeft = 0;
        float peakRight = 0;
        for (int i = 0; i + 1 < sampleCount; i += 2)
        {
            float left = samples[i];
            float right = samples[i + 1];
            sumLeft += left * left;
            sumRight += right * right;
            peakLeft = Math.Max(peakLeft, Math.Abs(left));
            peakRight = Math.Max(peakRight, Math.Abs(right));
        }

        float targetLeft = ToDb(sumLeft / channelFrames);
        float targetRight = ToDb(sumRight / channelFrames);
        float peakDbLeft = ToDb(peakLeft);
        float peakDbRight = ToDb(peakRight);
        long now = Environment.TickCount64;
        lock (_lock)
        {
            float elapsed = _lastWrite == 0 ? 5f : Math.Clamp(now - _lastWrite, 1f, 100f);
            _lastWrite = now;
            Smooth(ref _dbLeft, targetLeft, elapsed);
            Smooth(ref _dbRight, targetRight, elapsed);
            UpdatePeak(ref _peakDbLeft, ref _peakTickLeft, peakDbLeft, _dbLeft, now);
            UpdatePeak(ref _peakDbRight, ref _peakTickRight, peakDbRight, _dbRight, now);
        }
    }

    private static float ToDb(double meanSquare)
    {
        if (meanSquare <= 0.000000000001)
        {
            return MinimumDb;
        }

        return Math.Clamp(20f * MathF.Log10(MathF.Sqrt((float)meanSquare)), MinimumDb, 6f);
    }

    private static void Smooth(ref float current, float target, float elapsed)
    {
        target = Math.Clamp(target, MinimumDb, 6f);
        float time = target > current ? 40f : 260f;
        float coefficient = 1f - MathF.Exp(-elapsed / time);
        current += (target - current) * coefficient;
    }

    private static void UpdatePeak(ref float peak, ref long tick, float target, float level, long now)
    {
        if (target > peak)
        {
            peak = target;
            tick = now;
        }
        else
        {
            DecayPeak(ref peak, ref tick, now, level);
        }
    }

    private static void DecayPeak(ref float peak, ref long tick, long now, float level = MinimumDb)
    {
        if (tick == 0) return;
        long elapsed = now - tick;
        if (elapsed < 900) return;
        peak = Math.Max(level, peak - (elapsed - 900) * 0.018f);
        tick = now;
    }
}
