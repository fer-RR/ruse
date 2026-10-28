using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>Passes 48 kHz stereo audio to a 16 kHz mono tap for Whisper.</summary>
public sealed class TranscriptionProcessor : IAudioProcessor, IParameterizedProcessor, ITapProcessor
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
        WriteTap(TapId, samples, frames);
    }

    public void Reset()
    {
        if (Taps.TryGetValue(TapId, out var tap)) tap.Clear();
    }

    public static void EnsureTap(int id)
    {
        if (id > 0) Taps.GetOrAdd(id, _ => new TranscriptionTap());
    }

    /// <summary>Converts and appends one mixed block. Safe to call from any audio thread.</summary>
    public static void WriteTap(int id, ReadOnlySpan<float> samples, int frames)
    {
        if (Taps.TryGetValue(id, out var tap)) tap.Write(samples, frames);
    }

    public static int ReadAvailable(int id, byte[] destination) =>
        Taps.TryGetValue(id, out var tap) ? tap.Read(destination) : 0;

    /// <summary>Most recent peak after the anti-alias filter, for the node meter.</summary>
    public static float GetLevel(int id)
    {
        if (!Taps.TryGetValue(id, out var tap))
        {
            return 0f;
        }

        // Fade a stalled meter instead of freezing the last peak.
        long age = Environment.TickCount64 - tap.LastWriteTick;
        float level = tap.Level;
        if (age > 120)
        {
            level *= MathF.Max(0f, 1f - ((age - 120) / 1200f));
        }

        return level;
    }

    /// <summary>Tick count of the last block that carried audible signal, or 0.</summary>
    public static long GetLastAudioTick(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.LastAudioTick : 0;

    public static void ClearTap(int id)
    {
        if (Taps.TryGetValue(id, out var tap)) tap.Clear();
    }

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

internal sealed class TranscriptionTap
{
    private const int SampleRate = 16000;
    private const int Decimation = 3;
    private const int Capacity = SampleRate * 2 * 20;
    private const float SilenceFloor = 0.0005f;

    // Two low-pass biquads keep 8-24 kHz from folding into the 16 kHz stream.
    private static readonly Biquad AntiAliasA = Biquad.LowPass(6800f, 0.70710678f);
    private static readonly Biquad AntiAliasB = Biquad.LowPass(6800f, 0.70710678f);

    private readonly byte[] _buffer = new byte[Capacity];
    private readonly object _lock = new();
    private int _read;
    private int _write;
    private int _count;
    private int _phase;
    private float _z1A;
    private float _z2A;
    private float _z1B;
    private float _z2B;
    private float _level;
    private long _lastAudioTick;
    private long _lastWriteTick;

    public float Level => Volatile.Read(ref _level);
    public long LastAudioTick => Volatile.Read(ref _lastAudioTick);
    public long LastWriteTick => Volatile.Read(ref _lastWriteTick);

    public void Write(ReadOnlySpan<float> samples, int frames)
    {
        if (frames <= 0) return;
        int channels = Math.Max(1, samples.Length / frames);
        if (samples.Length < frames * channels) return;
        if (!Monitor.TryEnter(_lock)) return;

        try
        {
            long now = Environment.TickCount64;
            Volatile.Write(ref _lastWriteTick, now);
            for (int frame = 0; frame < frames; frame++)
            {
                int index = frame * channels;
                float mono = samples[index];
                for (int channel = 1; channel < channels; channel++)
                {
                    mono += samples[index + channel];
                }

                mono /= channels;
                mono = AntiAliasB.Process(AntiAliasA.Process(mono, ref _z1A, ref _z2A), ref _z1B, ref _z2B);

                float magnitude = MathF.Abs(mono);
                float level = Volatile.Read(ref _level);
                Volatile.Write(ref _level, MathF.Max(magnitude, level * 0.985f));
                if (magnitude > SilenceFloor)
                {
                    Volatile.Write(ref _lastAudioTick, now);
                }

                if (_phase != 0)
                {
                    _phase++;
                    if (_phase == Decimation) _phase = 0;
                    continue;
                }

                short value = (short)Math.Clamp(MathF.Round(mono * 32767f), -32768f, 32767f);
                Push((byte)value, (byte)(value >> 8));
                _phase = 1;
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private void Push(byte low, byte high)
    {
        if (_count + 2 > Capacity)
        {
            _read = (_read + 2) % Capacity;
            _count -= 2;
        }

        _buffer[_write] = low;
        _buffer[(_write + 1) % Capacity] = high;
        _write = (_write + 2) % Capacity;
        _count += 2;
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
            _phase = 0;
            _z1A = _z2A = _z1B = _z2B = 0;
            Volatile.Write(ref _level, 0f);
            Volatile.Write(ref _lastAudioTick, 0L);
            Volatile.Write(ref _lastWriteTick, 0L);
        }
    }

    private readonly record struct Biquad(float B0, float B1, float B2, float A1, float A2)
    {
        private const float SampleRate = 48000f;

        public static Biquad LowPass(float frequency, float q)
        {
            double w0 = 2.0 * Math.PI * frequency / SampleRate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * q);
            double a0 = 1.0 + alpha;
            return new Biquad(
                (float)((1.0 - cos) / 2.0 / a0),
                (float)((1.0 - cos) / a0),
                (float)((1.0 - cos) / 2.0 / a0),
                (float)(-2.0 * cos / a0),
                (float)((1.0 - alpha) / a0));
        }

        public float Process(float input, ref float z1, ref float z2)
        {
            float output = (B0 * input) + z1;
            z1 = (B1 * input) - (A1 * output) + z2;
            z2 = (B2 * input) - (A2 * output);
            return output;
        }
    }
}
