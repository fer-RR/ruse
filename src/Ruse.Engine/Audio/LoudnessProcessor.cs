using System.Collections.Concurrent;
using NAudio.Dsp;

namespace Ruse.App.Audio;

/// <summary>
/// Mastering meter tap: integrated loudness, short-term loudness, loudness range
/// (LRA) and true peak, following the ITU-R BS.1770 / EBU R128 model.
/// </summary>
public sealed class LoudnessProcessor : IAudioProcessor, IParameterizedProcessor, ITapProcessor
{
    public const string Id = "loudness";
    public const string TapParameterName = "__tap";

    private static readonly ConcurrentDictionary<int, LoudnessTap> Taps = new();
    private static int _nextTapId = Random.Shared.Next(1, 1_000_000);

    private volatile float _tapId;

    public LoudnessProcessor()
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
    public string DisplayName => "Loudness";
    public IReadOnlyList<AudioParameter> Parameters { get; }
    public int TapId => (int)MathF.Round(_tapId);
    public int LatencySamples => 0;

    public void SetTapId(int id)
    {
        _tapId = id;
        if (id > 0) Taps.GetOrAdd(id, _ => new LoudnessTap());
    }

    public void Process(Span<float> samples, int frames) => WriteTap(TapId, samples, frames);

    public static void WriteTap(int id, ReadOnlySpan<float> samples, int frames)
    {
        if (Taps.TryGetValue(id, out var tap)) tap.Write(samples, frames);
    }

    public static void ResetTap(int id)
    {
        if (Taps.TryGetValue(id, out var tap)) tap.Reset();
    }

    public static float GetIntegratedLufs(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.IntegratedLufs : -120f;

    public static float GetShortTermLufs(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.ShortTermLufs : -120f;

    public static float GetMomentaryLufs(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.MomentaryLufs : -120f;

    public static float GetLoudnessRange(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.LoudnessRange : 0f;

    public static float GetTruePeakDb(int id) =>
        Taps.TryGetValue(id, out var tap) ? tap.TruePeakDb : -120f;

    public static int NextTapId()
    {
        while (true)
        {
            int id = Interlocked.Increment(ref _nextTapId);
            if (!Taps.ContainsKey(id)) return id;
        }
    }

    public static void RemoveTap(int id) => Taps.TryRemove(id, out _);

    public void Reset() => ResetTap(TapId);
}

internal sealed class LoudnessTap
{
    private const int SampleRate = 48000;
    private const int SegmentFrames = SampleRate / 10; // 100 ms
    private const int BlockSegments = 4;               // 400 ms
    private const int ShortTermSegments = 30;           // 3 s
    private const int SegmentCapacity = 36_000;         // one hour at 10 segments/s
    private const int BlockCapacity = 36_000;
    private const int LraBins = 701;                    // -70..0 dB in 0.1 dB steps
    private const float AbsoluteGateLufs = -70f;
    private const float SilenceLufs = -120f;

    private readonly object _lock = new();
    private readonly float[] _segments = new float[SegmentCapacity];
    private readonly float[] _blocks = new float[BlockCapacity];
    private readonly int[] _lraBins = new int[LraBins];
    private readonly Oversampler _oversamplerL = new(4, SampleRate);
    private readonly Oversampler _oversamplerR = new(4, SampleRate);

    private int _segmentFrames;
    private double _sumL;
    private double _sumR;
    private int _segmentWrite;
    private int _segmentCount;
    private int _blockWrite;
    private int _blockCount;
    private int _shortTermCounter;

    private float _momentaryLufs = SilenceLufs;
    private float _shortTermLufs = SilenceLufs;
    private float _integratedLufs = SilenceLufs;
    private float _loudnessRange;
    private float _truePeak;

    // K-weighting filter state, per channel. The coefficients are the standard
    // 48 kHz BS.1770 pre-filter and RLB high-pass.
    private double _z1AL, _z2AL, _z1BL, _z2BL;
    private double _z1AR, _z2AR, _z1BR, _z2BR;

    public float MomentaryLufs
    {
        get { lock (_lock) return _momentaryLufs; }
    }

    public float ShortTermLufs
    {
        get { lock (_lock) return _shortTermLufs; }
    }

    public float IntegratedLufs
    {
        get { lock (_lock) return _integratedLufs; }
    }

    public float LoudnessRange
    {
        get { lock (_lock) return _loudnessRange; }
    }

    public float TruePeakDb
    {
        get
        {
            lock (_lock)
            {
                return _truePeak <= 1e-6f ? SilenceLufs : 20f * MathF.Log10(_truePeak);
            }
        }
    }

    public void Write(ReadOnlySpan<float> samples, int frames)
    {
        if (frames <= 0 || samples.IsEmpty) return;
        int channels = Math.Max(1, samples.Length / frames);

        lock (_lock)
        {
            Span<float> upsampled = stackalloc float[4];
            for (int frame = 0; frame < frames; frame++)
            {
                int index = frame * channels;
                float left = samples[index];
                float right = channels > 1 ? samples[index + 1] : left;

                _oversamplerL.Upsample(left, upsampled);
                for (int i = 0; i < upsampled.Length; i++)
                {
                    _truePeak = MathF.Max(_truePeak, MathF.Abs(upsampled[i]));
                }

                _oversamplerR.Upsample(right, upsampled);
                for (int i = 0; i < upsampled.Length; i++)
                {
                    _truePeak = MathF.Max(_truePeak, MathF.Abs(upsampled[i]));
                }

                double weightedL = KWeight(left, ref _z1AL, ref _z2AL, ref _z1BL, ref _z2BL);
                double weightedR = KWeight(right, ref _z1AR, ref _z2AR, ref _z1BR, ref _z2BR);
                _sumL += weightedL * weightedL;
                _sumR += weightedR * weightedR;
                _segmentFrames++;

                if (_segmentFrames >= SegmentFrames)
                {
                    CommitSegment();
                    _segmentFrames = 0;
                    _sumL = 0;
                    _sumR = 0;
                }
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_segments, 0, _segments.Length);
            Array.Clear(_blocks, 0, _blocks.Length);
            Array.Clear(_lraBins, 0, _lraBins.Length);
            _segmentFrames = 0;
            _sumL = 0;
            _sumR = 0;
            _segmentWrite = 0;
            _segmentCount = 0;
            _blockWrite = 0;
            _blockCount = 0;
            _shortTermCounter = 0;
            _momentaryLufs = SilenceLufs;
            _shortTermLufs = SilenceLufs;
            _integratedLufs = SilenceLufs;
            _loudnessRange = 0f;
            _truePeak = 0;
            _z1AL = _z2AL = _z1BL = _z2BL = 0;
            _z1AR = _z2AR = _z1BR = _z2BR = 0;
            _oversamplerL.Reset();
            _oversamplerR.Reset();
        }
    }

    private void CommitSegment()
    {
        float power = (float)((_sumL + _sumR) / Math.Max(1, _segmentFrames));
        _segments[_segmentWrite] = power;
        _segmentWrite = (_segmentWrite + 1) % SegmentCapacity;
        if (_segmentCount < SegmentCapacity) _segmentCount++;

        if (_segmentCount >= BlockSegments)
        {
            float average = AverageSegments(BlockSegments);
            _momentaryLufs = ToLufs(average);
            _blocks[_blockWrite] = average;
            _blockWrite = (_blockWrite + 1) % BlockCapacity;
            if (_blockCount < BlockCapacity) _blockCount++;

            // Keep UI reads cheap: recompute the full-range values once per
            // second instead of scanning every block while holding the write lock.
            if (_blockCount >= BlockSegments && (_blockCount == BlockSegments || _blockCount % 10 == 0))
            {
                _integratedLufs = ComputeIntegrated();
                _loudnessRange = ComputeLoudnessRange(_integratedLufs);
            }
        }

        if (_segmentCount >= ShortTermSegments)
        {
            _shortTermCounter++;
            if (_shortTermCounter >= 10)
            {
                _shortTermCounter = 0;
                _shortTermLufs = ToLufs(AverageSegments(ShortTermSegments));
                AddLraBin(_shortTermLufs);
            }
        }
    }

    private float AverageSegments(int count)
    {
        int start = (_segmentWrite - count + SegmentCapacity) % SegmentCapacity;
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += _segments[(start + i) % SegmentCapacity];
        }

        return (float)(sum / count);
    }

    private float ComputeIntegrated()
    {
        if (_blockCount < BlockSegments) return SilenceLufs;

        float absolutePower = MathF.Pow(10f, (AbsoluteGateLufs + 0.691f) / 10f);
        double sum = 0;
        int count = 0;
        for (int i = 0; i < _blockCount; i++)
        {
            float power = _blocks[i];
            if (power >= absolutePower)
            {
                sum += power;
                count++;
            }
        }

        if (count == 0) return SilenceLufs;
        double integrated = -0.691 + (10.0 * Math.Log10(sum / count));
        float relativePower = (float)Math.Pow(10.0, (integrated - 10.0 + 0.691) / 10.0);

        sum = 0;
        count = 0;
        for (int i = 0; i < _blockCount; i++)
        {
            float power = _blocks[i];
            if (power >= absolutePower && power >= relativePower)
            {
                sum += power;
                count++;
            }
        }

        return count == 0 ? SilenceLufs : (float)(-0.691 + (10.0 * Math.Log10(sum / count)));
    }

    private float ComputeLoudnessRange() => ComputeLoudnessRange(ComputeIntegrated());

    private float ComputeLoudnessRange(float integrated)
    {
        if (integrated <= SilenceLufs + 1f) return 0f;

        int startBin = Math.Clamp(
            (int)Math.Ceiling((integrated - 20f + 70f) * 10f), 0, LraBins - 1);
        int total = 0;
        for (int i = startBin; i < LraBins; i++) total += _lraBins[i];
        if (total < 2) return 0f;

        int lowTarget = Math.Max(1, (int)Math.Round(total * 0.10));
        int highTarget = Math.Max(1, (int)Math.Round(total * 0.95));
        int cumulative = 0;
        int lowBin = startBin;
        int highBin = LraBins - 1;

        for (int i = startBin; i < LraBins; i++)
        {
            cumulative += _lraBins[i];
            if (cumulative >= lowTarget)
            {
                lowBin = i;
                break;
            }
        }

        cumulative = 0;
        for (int i = startBin; i < LraBins; i++)
        {
            cumulative += _lraBins[i];
            if (cumulative >= highTarget)
            {
                highBin = i;
                break;
            }
        }

        return (highBin - lowBin) / 10f;
    }

    private void AddLraBin(float lufs)
    {
        if (lufs <= AbsoluteGateLufs) return;
        int bin = (int)MathF.Round((lufs + 70f) * 10f);
        if ((uint)bin < (uint)_lraBins.Length) _lraBins[bin]++;
    }

    private static float ToLufs(float power) =>
        power <= 1e-12f ? SilenceLufs : -0.691f + (10f * MathF.Log10(power));

    private static double KWeight(
        float input,
        ref double z1A, ref double z2A,
        ref double z1B, ref double z2B)
    {
        double x = input;
        double y = (1.53512485958697 * x) + z1A;
        z1A = (-2.69169618940638 * x) - (-1.69065929318241 * y) + z2A;
        z2A = (1.19839281085285 * x) - (0.73248077421585 * y);

        double z = y;
        double output = z + z1B;
        z1B = (-2.0 * z) - (-1.99004745483398 * output) + z2B;
        z2B = z - (0.99007225036621 * output);
        return output;
    }
}
