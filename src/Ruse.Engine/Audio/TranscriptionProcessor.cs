using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>Pass-through modifier that exposes 16 kHz mono PCM for offline transcription.</summary>
public sealed class TranscriptionProcessor : IAudioProcessor, IParameterizedProcessor
{
    public const string Id = "transcriber";
    public const string TapParameterName = "__tap";

    private static readonly ConcurrentDictionary<int, TranscriptionTap> Taps = new();
    private static int _nextTapId = Random.Shared.Next(1, 1_000_000);
    private volatile float _tapId;

    public TranscriptionProcessor()
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
    public string DisplayName => "Transcriber";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int TapId => (int)MathF.Round(_tapId);

    public void SetTapId(int id)
    {
        _tapId = id;
        EnsureTap(id);
    }

    public void Process(Span<float> samples, int frames)
    {
        if (Taps.TryGetValue(TapId, out var tap)) tap.Write(samples, frames);
    }

    public void Reset()
    {
        if (Taps.TryGetValue(TapId, out var tap)) tap.Clear();
    }

    public static void EnsureTap(int id)
    {
        if (id > 0) Taps.GetOrAdd(id, _ => new TranscriptionTap());
    }

    public static int ReadAvailable(int id, byte[] destination) =>
        Taps.TryGetValue(id, out var tap) ? tap.Read(destination) : 0;

    public static int NextTapId()
    {
        while (true)
        {
            int id = Interlocked.Increment(ref _nextTapId);
            if (!Taps.ContainsKey(id)) return id;
        }
    }
}

internal sealed class TranscriptionTap
{
    private const int SampleRate = 16000;
    private const int Capacity = SampleRate * 2 * 20;
    private readonly byte[] _buffer = new byte[Capacity];
    private readonly object _lock = new();
    private int _read;
    private int _write;
    private int _count;

    public void Write(ReadOnlySpan<float> samples, int frames)
    {
        if (frames < 3 || samples.Length < frames * 2) return;
        if (!Monitor.TryEnter(_lock)) return;

        try
        {
            for (int frame = 0; frame + 2 < frames; frame += 3)
            {
                int index = frame * 2;
                float mono = (samples[index] + samples[index + 1] +
                              samples[index + 2] + samples[index + 3] +
                              samples[index + 4] + samples[index + 5]) / 6f;
                short value = (short)Math.Clamp(mono * 32767f, -32768f, 32767f);

                if (_count + 2 > Capacity)
                {
                    _read = (_read + 2) % Capacity;
                    _count -= 2;
                }

                _buffer[_write] = (byte)value;
                _buffer[(_write + 1) % Capacity] = (byte)(value >> 8);
                _write = (_write + 2) % Capacity;
                _count += 2;
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    public int Read(byte[] destination)
    {
        lock (_lock)
        {
            int count = Math.Min(destination.Length, _count);
            count -= count % 2;
            for (int i = 0; i < count; i++)
            {
                destination[i] = _buffer[_read];
                _read = (_read + 1) % Capacity;
            }

            _count -= count;
            return count;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _read = _write = _count = 0;
        }
    }
}
