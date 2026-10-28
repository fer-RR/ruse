using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>Pass-through modifier that exposes a scrolling route scope for the UI.</summary>
public sealed class WaveformProcessor : IAudioProcessor, IParameterizedProcessor, ITapProcessor
{
    public const string Id = "waveform";
    public const string TapParameterName = "__tap";
    public const int Points = 96;

    private static readonly ConcurrentDictionary<int, WaveformTap> Taps = new();
    private static int _nextTapId = Random.Shared.Next(1, 1_000_000);
    private volatile float _tapId;
    private volatile float _gain = 1f;
    private volatile float _smoothing;
    private volatile float _speed = 1f;

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
                Name = "Gain",
                Min = 0.1f,
                Max = 4f,
                Get = () => _gain,
                Set = value => _gain = Math.Clamp(value, 0.1f, 4f),
                Unit = "x",
            },
            new AudioParameter
            {
                Name = "Smoothing",
                Min = 0f,
                Max = 95f,
                Get = () => _smoothing * 100f,
                Set = value => _smoothing = Math.Clamp(value, 0f, 95f) / 100f,
                Unit = "%",
            },
            new AudioParameter
            {
                Name = "Scroll speed",
                Min = 0.25f,
                Max = 4f,
                Get = () => _speed,
                Set = value => _speed = Math.Clamp(value, 0.25f, 4f),
                Unit = "x",
            },
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Waveform";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int TapId => (int)MathF.Round(_tapId);
    public float Gain => _gain;
    public float Smoothing => _smoothing;
    public float Speed => _speed;

    public void SetTapId(int id)
    {
        _tapId = id;
        EnsureTap(id);
    }

    public void Process(Span<float> samples, int frames)
    {
        WriteTap(TapId, samples, frames, Gain, Smoothing, Speed);
    }

    /// <summary>Updates a tap from a mixed block. Called on the monitor thread.</summary>
    public static void WriteTap(int id, ReadOnlySpan<float> samples, int frames,
        float gain = 1f, float smoothing = 0f, float speed = 1f)
    {
        if (Taps.TryGetValue(id, out var tap))
        {
            tap.Write(samples, frames, gain, smoothing, speed);
        }
    }

    public void Reset()
    {
        if (Taps.TryGetValue(TapId, out var tap)) tap.Clear();
    }

    public static void EnsureTap(int id)
    {
        if (id > 0) Taps.GetOrAdd(id, _ => new WaveformTap());
    }

    public static float[]? GetSamples(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.Snapshot() : null;

    public static void RemoveTap(int id) => Taps.TryRemove(id, out _);

    public static int NextTapId()
    {
        while (true)
        {
            int id = Interlocked.Increment(ref _nextTapId);
            if (!Taps.ContainsKey(id)) return id;
        }
    }
}

internal sealed class WaveformTap
{
    private readonly float[] _samples = new float[WaveformProcessor.Points];
    private readonly object _lock = new();
    private int _write;
    private float _smooth;
    private float _speedCarry;

    public void Write(ReadOnlySpan<float> samples, int frames, float gain, float smoothing, float speed)
    {
        if (frames <= 0 || samples.Length < 2) return;
        int channels = Math.Max(1, samples.Length / frames);
        if (samples.Length < frames * channels) return;

        lock (_lock)
        {
            _speedCarry += Math.Clamp(speed, 0.25f, 4f);
            int append = (int)MathF.Floor(_speedCarry);
            _speedCarry -= append;
            if (append <= 0) return;
            append = Math.Min(append, WaveformProcessor.Points / 2);

            for (int point = 0; point < append; point++)
            {
                int first = frames * point / append;
                int last = Math.Max(first + 1, frames * (point + 1) / append);
                float peak = 0f;
                for (int frame = first; frame < last && frame < frames; frame++)
                {
                    int index = frame * channels;
                    float mono = samples[index];
                    for (int channel = 1; channel < channels; channel++)
                    {
                        mono += samples[index + channel];
                    }

                    mono /= channels;
                    if (MathF.Abs(mono) > MathF.Abs(peak)) peak = mono;
                }

                peak *= Math.Clamp(gain, 0.1f, 4f);
                _smooth = _smooth * Math.Clamp(smoothing, 0f, 0.95f) +
                          peak * (1f - Math.Clamp(smoothing, 0f, 0.95f));
                _samples[_write] = Math.Clamp(_smooth, -1f, 1f);
                _write = (_write + 1) % WaveformProcessor.Points;
            }
        }
    }

    public float[] Snapshot()
    {
        lock (_lock)
        {
            var result = new float[WaveformProcessor.Points];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = _samples[(_write + i) % result.Length];
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_samples);
            _write = 0;
            _smooth = 0f;
            _speedCarry = 0f;
        }
    }
}
