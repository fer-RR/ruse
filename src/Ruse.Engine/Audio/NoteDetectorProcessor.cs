using System.Collections.Concurrent;

namespace Ruse.App.Audio;

/// <summary>
/// Monophonic note analysis for vocals and single instruments. The detector
/// runs on the tap worker so it never enters the audible chain; Auto-tune reads
/// the latest stable note from <see cref="TryGetReading"/>.
/// </summary>
public sealed class NoteDetectorProcessor : IAudioProcessor, ITapProcessor
{
    public const string Id = "note-detector";

    private static readonly string[] NoteNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static readonly ConcurrentDictionary<int, NoteState> States = new();
    private static int _nextTapId;

    public int TapId { get; private set; }

    public string TypeId => Id;
    public string DisplayName => "Note detector";
    public int LatencySamples => 0;

    public void SetTapId(int tapId)
    {
        TapId = tapId;
        if (tapId > 0)
        {
            States.TryAdd(tapId, new NoteState());
        }
    }

    public static int NextTapId() => Interlocked.Increment(ref _nextTapId);

    public static void EnsureTap(int tapId)
    {
        if (tapId > 0)
        {
            States.TryAdd(tapId, new NoteState());
        }
    }

    public static void RemoveTap(int tapId) => States.TryRemove(tapId, out _);

    public static void WriteTap(int tapId, ReadOnlySpan<float> samples, int frames)
    {
        if (tapId <= 0 || frames <= 0)
        {
            return;
        }

        States.GetOrAdd(tapId, _ => new NoteState()).Write(samples, frames);
    }

    public static bool TryGetReading(int tapId, out NoteReading reading)
    {
        reading = default;
        if (tapId <= 0 || !States.TryGetValue(tapId, out var state))
        {
            return false;
        }

        return state.TryGetReading(out reading);
    }

    public void Process(Span<float> samples, int frames)
    {
        // Analysis runs in the tap worker; this processor is intentionally
        // absent from the audible chain.
    }

    public void Reset()
    {
        if (TapId > 0)
        {
            States.TryRemove(TapId, out _);
            States.TryAdd(TapId, new NoteState());
        }
    }

    private sealed class NoteState
    {
        private const int StableBlocks = 4;
        private const int SilenceBlocks = 10;
        private const int StaleMs = 250;
        private const float MinConfidence = 0.35f;
        private const float ConfidenceMargin = 0.06f;

        private readonly object _sync = new();
        private readonly NotePitchTracker _detector = new(highResolution: true);
        private int _candidateMidi = -1;
        private float _candidateConfidence;
        private int _stable;
        private int _silence;
        private NoteReading _reading;

        public void Write(ReadOnlySpan<float> samples, int frames)
        {
            _detector.Add(samples, frames);
            if (!_detector.TryGetPitch(out float hz) || _detector.Confidence < MinConfidence)
            {
                _silence++;
                if (_silence >= SilenceBlocks)
                {
                    lock (_sync)
                    {
                        _candidateMidi = -1;
                        _candidateConfidence = 0;
                        _stable = 0;
                        _reading = default;
                    }
                }

                return;
            }

            _silence = 0;
            double midi = 69.0 + 12.0 * Math.Log2(hz / 440.0);
            int nearest = (int)Math.Round(midi);
            float cents = (float)((midi - nearest) * 100.0);
            float confidence = _detector.Confidence;
            if (nearest != _candidateMidi)
            {
                _candidateMidi = nearest;
                _candidateConfidence = confidence;
                _stable = 0;
            }
            else
            {
                _candidateConfidence = Math.Max(_candidateConfidence, confidence);
            }

            _stable++;
            if (_stable < StableBlocks)
            {
                return;
            }

            long now = Environment.TickCount64;
            bool currentValid = _reading.Midi > 0 && now - _reading.UpdatedAt <= StaleMs;
            if (currentValid && nearest != _reading.Midi &&
                _candidateConfidence < _reading.Confidence + ConfidenceMargin)
            {
                return;
            }

            var reading = new NoteReading(
                nearest,
                NameFor(nearest),
                cents,
                hz,
                _candidateConfidence,
                now);
            lock (_sync)
            {
                _reading = reading;
            }
        }

        public bool TryGetReading(out NoteReading reading)
        {
            lock (_sync)
            {
                reading = _reading;
            }

            if (reading.Midi <= 0 ||
                Environment.TickCount64 - reading.UpdatedAt > StaleMs)
            {
                reading = default;
                return false;
            }

            return true;
        }

        private static string NameFor(int midi)
        {
            int pitchClass = ((midi % 12) + 12) % 12;
            int octave = midi / 12 - 1;
            return NoteNames[pitchClass] + octave.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>Normalized autocorrelation tracker tuned for a stable sung note.</summary>
internal sealed class NotePitchTracker
{
    private readonly int _sampleRate;
    private readonly int _decimation;
    private readonly int _windowSize;
    private readonly int _hop;
    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly float[] _ring;
    private readonly float[] _window;
    private readonly double[] _correlation;
    private int _write;
    private int _filled;
    private int _since;
    private float _decimateSum;
    private int _decimateCount;
    private float _pitch;
    private float _confidence;
    private bool _hasPitch;

    public NotePitchTracker(bool highResolution = true)
    {
        _sampleRate = highResolution ? 24000 : 12000;
        _decimation = highResolution ? 2 : 4;
        _windowSize = highResolution ? 2048 : 1024;
        _hop = highResolution ? 480 : 240;
        _minLag = highResolution ? 20 : 10;
        _maxLag = highResolution ? 1200 : 600;
        _ring = new float[_windowSize];
        _window = new float[_windowSize];
        _correlation = new double[_maxLag + 1];
    }

    public float Confidence => _confidence;

    public void Add(ReadOnlySpan<float> samples, int frames)
    {
        if (frames <= 0 || samples.Length < frames * 2)
        {
            return;
        }

        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * 2;
            _decimateSum += (samples[index] + samples[index + 1]) * 0.5f;
            _decimateCount++;
            if (_decimateCount < _decimation)
            {
                continue;
            }

            _ring[_write] = _decimateSum / _decimation;
            _decimateSum = 0;
            _decimateCount = 0;
            _write = (_write + 1) % _windowSize;
            if (_filled < _windowSize)
            {
                _filled++;
            }

            _since++;
        }

        if (_filled < _windowSize || _since < _hop)
        {
            return;
        }

        _since = 0;
        Detect();
    }

    public bool TryGetPitch(out float hz)
    {
        hz = _pitch;
        return _hasPitch;
    }

    public void Reset()
    {
        Array.Clear(_ring);
        _write = 0;
        _filled = 0;
        _since = 0;
        _pitch = 0f;
        _confidence = 0f;
        _hasPitch = false;
    }

    private void Detect()
    {
        int start = _write;
        double energy = 0;
        for (int i = 0; i < _windowSize; i++)
        {
            float sample = _ring[(start + i) % _windowSize];
            _window[i] = sample;
            energy += sample * sample;
        }

        if (energy / _windowSize < 1e-7)
        {
            _confidence = 0f;
            _hasPitch = false;
            return;
        }

        int bestLag = 0;
        double best = 0;
        for (int lag = _minLag; lag <= _maxLag; lag++)
        {
            double correlation = 0;
            double energyA = 0;
            double energyB = 0;
            int limit = _windowSize - lag;
            for (int i = 0; i < limit; i++)
            {
                float a = _window[i];
                float b = _window[i + lag];
                correlation += a * b;
                energyA += a * a;
                energyB += b * b;
            }

            double denominator = Math.Sqrt(energyA * energyB);
            if (denominator <= 1e-12)
            {
                continue;
            }

            double normalized = correlation / denominator;
            _correlation[lag] = normalized;
            if (normalized > best)
            {
                best = normalized;
                bestLag = lag;
            }
        }

        if (bestLag == 0 || best < 0.45)
        {
            _confidence = 0f;
            _hasPitch = false;
            return;
        }

        double threshold = Math.Max(0.5, best * 0.85);
        for (int lag = _minLag + 1; lag < _maxLag; lag++)
        {
            if (_correlation[lag] >= threshold &&
                _correlation[lag] >= _correlation[lag - 1] &&
                _correlation[lag] >= _correlation[lag + 1])
            {
                bestLag = lag;
                best = _correlation[lag];
                break;
            }
        }

        double refined = bestLag;
        if (bestLag > _minLag && bestLag < _maxLag)
        {
            double previous = _correlation[bestLag - 1];
            double current = _correlation[bestLag];
            double next = _correlation[bestLag + 1];
            double denominator = 2.0 * (2.0 * current - previous - next);
            if (Math.Abs(denominator) > 1e-9)
            {
                refined += (previous - next) / denominator;
            }
        }

        float pitch = (float)(_sampleRate / refined);
        if (pitch >= 200f && TryZeroCrossing(out float crossingHz))
        {
            pitch = crossingHz;
        }

        _pitch = pitch;
        _confidence = (float)Math.Clamp((best - 0.45) / 0.55, 0, 1);
        _hasPitch = _pitch >= 30f && _pitch <= 1100f;
    }

    private bool TryZeroCrossing(out float hz)
    {
        double first = -1;
        double last = -1;
        int crossings = 0;
        float previous = _window[0];
        for (int i = 1; i < _windowSize; i++)
        {
            float current = _window[i];
            if (previous <= 0f && current > 0f)
            {
                double fraction = previous == current ? 0 : -previous / (current - previous);
                double position = i - 1 + fraction;
                if (first < 0)
                {
                    first = position;
                }
                else
                {
                    last = position;
                    crossings++;
                }
            }

            previous = current;
        }

        if (crossings < 4 || last <= first)
        {
            hz = 0;
            return false;
        }

        hz = (float)(crossings * _sampleRate / (last - first));
        return hz >= 200f && hz <= 1100f;
    }
    private double Correlation(int lag)
    {
        double correlation = 0;
        double energyA = 0;
        double energyB = 0;
        int limit = _windowSize - lag;
        for (int i = 0; i < limit; i++)
        {
            float a = _window[i];
            float b = _window[i + lag];
            correlation += a * b;
            energyA += a * a;
            energyB += b * b;
        }

        double denominator = Math.Sqrt(energyA * energyB);
        return denominator <= 1e-12 ? 0 : correlation / denominator;
    }
}
public readonly record struct NoteReading(
    int Midi,
    string Name,
    float Cents,
    float Hz,
    float Confidence,
    long UpdatedAt);
