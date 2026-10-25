using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>Pass-through modifier that exposes the last route block for the UI scope.</summary>
public sealed class WaveformProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "waveform";
    public const string TapParameterName = "__tap";
    public const int Points = 96;

    private static readonly ConcurrentDictionary<int, WaveformTap> Taps = new();
    private static int _nextTapId = Random.Shared.Next(1, 1_000_000);
    private volatile float _tapId;

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
        };
    }

    public string TypeId => Id;
    public string DisplayName => "Waveform";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int TapId => (int)MathF.Round(_tapId);

    public void SetTapId(int id)
    {
        _tapId = id;
        EnsureTap(id);
    }

    public void Process(Span<float> samples, int frames)
    {
        if (Taps.TryGetValue(TapId, out var tap))
        {
            tap.Write(samples, frames);
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
    private int _write;

    public void Write(ReadOnlySpan<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length < 2) return;
        int channels = Math.Max(1, samples.Length / frames);
        int step = Math.Max(1, frames / WaveformProcessor.Points);
        int write = Volatile.Read(ref _write);

        for (int point = 0; point < WaveformProcessor.Points; point++)
        {
            int frame = Math.Min(frames - 1, point * step);
            int index = frame * channels;
            float left = samples[index];
            float right = channels > 1 ? samples[Math.Min(index + 1, samples.Length - 1)] : left;
            _samples[write] = (left + right) * 0.5f;
            write = (write + 1) % WaveformProcessor.Points;
        }

        Volatile.Write(ref _write, write);
    }

    public float[] Snapshot()
    {
        var result = new float[WaveformProcessor.Points];
        int write = Volatile.Read(ref _write);
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = _samples[(write + i) % result.Length];
        }

        return result;
    }

    public void Clear()
    {
        Array.Clear(_samples);
        Volatile.Write(ref _write, 0);
    }
}
