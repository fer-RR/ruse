using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>Pass-through tap that exposes a triggered time-domain window.</summary>
public sealed class WaveformProcessor : IAudioProcessor, IParameterizedProcessor, ITapProcessor, INativeProcessor
{
    public const string Id = "waveform";
    public const string TapParameterName = "__tap";
    public const int SampleCount = 256;
    public const int TriggerRising = 0;
    public const int TriggerFalling = 1;
    public const int TriggerOff = 2;

    private static readonly ConcurrentDictionary<int, WaveformTap> Taps = new();
    private static int _nextTapId = Random.Shared.Next(1, 1_000_000);

    private volatile float _tapId;
    private volatile float _trigger = TriggerRising;

    public WaveformProcessor()
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
                Name = "Trigger",
                Min = TriggerRising,
                Max = TriggerOff,
                Default = TriggerRising,
                IsChoice = true,
                IsInteger = true,
                Options = new[] { "Rising", "Falling", "Off" },
                Get = () => _trigger,
                Set = value => _trigger = Math.Clamp(MathF.Round(value), TriggerRising, TriggerOff),
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Waveform";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int TapId => (int)MathF.Round(_tapId);
    public int Trigger => (int)MathF.Round(_trigger);

    public void SetTapId(int id)
    {
        _tapId = id;
        if (id > 0) Taps.GetOrAdd(id, _ => new WaveformTap());
    }

    public void Process(Span<float> samples, int frames) => WriteTap(TapId, samples, frames);

    public static void WriteTap(int id, ReadOnlySpan<float> samples, int frames)
    {
        if (Taps.TryGetValue(id, out var tap)) tap.Write(samples, frames);
    }

    public void Reset()
    {
        if (TapId > 0)
        {
            Taps[TapId] = new WaveformTap();
        }
    }

    public static WaveformReading? GetReading(int id, int trigger) =>
        Taps.TryGetValue(id, out var tap) ? tap.Snapshot(trigger) : null;

    public static int NextTapId() => Interlocked.Increment(ref _nextTapId);
    public static void RemoveTap(int id) => Taps.TryRemove(id, out _);
}

public readonly record struct WaveformReading(float[] Samples, float Peak);

internal sealed class WaveformTap
{
    private const int Count = WaveformProcessor.SampleCount;
    private const int RingCount = 2048;
    private const int SearchSpan = 1024;
    private readonly object _lock = new();
    private readonly float[] _samples = new float[RingCount];
    private int _write;
    private int _filled;

    public void Write(ReadOnlySpan<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length < frames * 2) return;
        lock (_lock)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                int index = frame * 2;
                // Keep the louder channel so one-sided clipping stays visible.
                float left = samples[index];
                float right = samples[index + 1];
                _samples[_write] = MathF.Abs(left) >= MathF.Abs(right) ? left : right;
                _write = (_write + 1) % RingCount;
                if (_filled < RingCount) _filled++;
            }
        }
    }

    public WaveformReading Snapshot(int trigger)
    {
        lock (_lock)
        {
            var result = new float[Count];
            if (_filled < Count)
            {
                Array.Copy(_samples, result, _filled);
            }
            else
            {
                int start = (_write - Count + RingCount) % RingCount;
                if (trigger != WaveformProcessor.TriggerOff)
                {
                    int limit = Math.Min(SearchSpan, _filled - Count);
                    for (int offset = 0; offset < limit; offset++)
                    {
                        int index = (_write - Count - offset + RingCount * 2) %
                                    RingCount;
                        if (_filled < RingCount && index == 0)
                        {
                            continue;
                        }

                        int previous = (index - 1 + RingCount) % RingCount;
                        bool rising = _samples[previous] < 0f && _samples[index] >= 0f;
                        bool falling = _samples[previous] >= 0f && _samples[index] < 0f;
                        if (trigger == WaveformProcessor.TriggerRising ? rising : falling)
                        {
                            start = index;
                            break;
                        }
                    }
                }

                for (int i = 0; i < Count; i++)
                {
                    result[i] = _samples[(start + i) % RingCount];
                }
            }

            float peak = 0f;
            for (int i = 0; i < result.Length; i++)
            {
                float magnitude = MathF.Abs(result[i]);
                if (magnitude > peak) peak = magnitude;
            }

            return new WaveformReading(result, peak);
        }
    }
}
