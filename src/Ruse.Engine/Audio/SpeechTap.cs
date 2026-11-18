using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>
/// Shared 16 kHz mono speech stream. The audio thread writes 48 kHz interleaved
/// blocks; VAD/ASR workers read 16 kHz mono samples with timestamps preserved.
/// </summary>
public sealed class SpeechTap
{
    public const int SampleRate = 16000;
    private const int Decimation = 3;
    private const int Capacity = SampleRate * 20;
    private const float SilenceFloor = 0.0005f;

    private static readonly Biquad AntiAliasA = Biquad.LowPass(6800f, 0.70710678f);
    private static readonly Biquad AntiAliasB = Biquad.LowPass(6800f, 0.70710678f);

    private readonly float[] _buffer = new float[Capacity];
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
    private long _totalSamples;
    private long _generation;
    private long _lastAudioTick;
    private long _lastWriteTick;

    public float Level => Volatile.Read(ref _level);
    public long TotalSamples => Volatile.Read(ref _totalSamples);
    public long Generation => Volatile.Read(ref _generation);
    public long LastAudioTick => Volatile.Read(ref _lastAudioTick);
    public long LastWriteTick => Volatile.Read(ref _lastWriteTick);
    public int Available { get { lock (_lock) return _count; } }

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

                Push(mono);
                _phase = 1;
            }

            _z1A = DenormalMath.Flush(_z1A);
            _z2A = DenormalMath.Flush(_z2A);
            _z1B = DenormalMath.Flush(_z1B);
            _z2B = DenormalMath.Flush(_z2B);
            Volatile.Write(ref _level, DenormalMath.Flush(Volatile.Read(ref _level)));
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private void Push(float sample)
    {
        if (_count >= Capacity)
        {
            _read = (_read + 1) % Capacity;
            _count--;
        }

        _buffer[_write] = sample;
        _write = (_write + 1) % Capacity;
        _count++;
        _totalSamples++;
    }

    /// <summary>Reads available 16 kHz mono samples. Returns the number written.</summary>
    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty) return 0;
        lock (_lock)
        {
            int count = Math.Min(destination.Length, _count);
            for (int index = 0; index < count; index++)
            {
                destination[index] = _buffer[_read];
                _read = (_read + 1) % Capacity;
            }

            _count -= count;
            return count;
        }
    }

    /// <summary>Compatibility reader for the existing byte-based Whisper path.</summary>
    public int ReadBytes(byte[] destination)
    {
        if (destination.Length < 2) return 0;
        lock (_lock)
        {
            int samples = Math.Min(destination.Length / 2, _count);
            for (int index = 0; index < samples; index++)
            {
                short value = (short)Math.Clamp(MathF.Round(_buffer[_read] * 32767f), -32768f, 32767f);
                destination[index * 2] = (byte)value;
                destination[index * 2 + 1] = (byte)(value >> 8);
                _read = (_read + 1) % Capacity;
            }

            _count -= samples;
            return samples * 2;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _read = _write = _count = 0;
            _phase = 0;
            _z1A = _z2A = _z1B = _z2B = 0;
            _totalSamples = 0;
            _generation++;
            Volatile.Write(ref _level, 0f);
            Volatile.Write(ref _lastAudioTick, 0L);
            Volatile.Write(ref _lastWriteTick, 0L);
        }
    }

    private readonly record struct Biquad(float B0, float B1, float B2, float A1, float A2)
    {
        private const float InputSampleRate = 48000f;

        public static Biquad LowPass(float frequency, float q)
        {
            double w0 = 2.0 * Math.PI * frequency / InputSampleRate;
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

/// <summary>Registry of shared speech taps keyed by processor tap id.</summary>
public static class SpeechTapRegistry
{
    private static readonly ConcurrentDictionary<int, SpeechTap> Taps = new();

    public static SpeechTap GetOrAdd(int id) => Taps.GetOrAdd(id, _ => new SpeechTap());

    public static bool TryGet(int id, out SpeechTap tap) => Taps.TryGetValue(id, out tap!);

    public static void Remove(int id) => Taps.TryRemove(id, out _);
}
