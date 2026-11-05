namespace Ruse.App.Audio;

/// <summary>
/// Runs a pitch tracker on a background thread so vocal analysis never blocks
/// the render callback. The newest confident pitch is published for the tuner;
/// stale readings expire so a stopped worker cannot hold a correction forever.
/// </summary>
internal sealed class PitchAnalysisWorker : IDisposable
{
    private const int SampleRate = 48000;
    private const int MaxBlockFrames = 480;
    private const int StaleMs = 250;

    private readonly FloatRingBuffer _ring = new(SampleRate * 2 * 2);
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly object _trackerLock = new();
    private readonly float[] _scratch = new float[MaxBlockFrames * 2];
    private readonly bool _highResolution;
    private IPitchTracker _tracker;
    private PitchTrackerMode _mode;
    private PitchReading? _reading;
    private long _publishedAt;
    private int _resetRequested;
    private volatile bool _running = true;

    public PitchAnalysisWorker(bool highResolution, PitchTrackerMode mode)
    {
        _highResolution = highResolution;
        _mode = mode;
        _tracker = PitchTrackerFactory.Create(mode, highResolution);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Ruse pitch analysis",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>Audio-thread entry point: copy the block and wake the worker.</summary>
    public void Submit(ReadOnlySpan<float> samples, int frames)
    {
        if (!_running || frames <= 0)
        {
            return;
        }

        int floats = Math.Min(frames, MaxBlockFrames) * 2;
        if (samples.Length < floats)
        {
            return;
        }

        _ring.Write(samples[..floats]);
        _signal.Set();
    }

    public bool TryGetPitch(out float hz, out float confidence)
    {
        PitchReading? reading = Volatile.Read(ref _reading);
        if (reading == null || Environment.TickCount64 - reading.At > StaleMs)
        {
            hz = 0f;
            confidence = 0f;
            return false;
        }

        hz = reading.Hz;
        confidence = reading.Confidence;
        return reading.Valid;
    }

    public void SetTracker(PitchTrackerMode mode)
    {
        lock (_trackerLock)
        {
            if (_mode == mode)
            {
                return;
            }

            _mode = mode;
            _tracker = PitchTrackerFactory.Create(mode, _highResolution);
        }
    }

    public void Reset()
    {
        // Reset can be called from the render thread after a fault, so never
        // wait for the analysis lock here. The worker applies the reset.
        _ring.Clear();
        Volatile.Write(ref _reading, null);
        Interlocked.Exchange(ref _resetRequested, 1);
        _signal.Set();
    }

    public void Dispose()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _signal.Set();
        if (Thread.CurrentThread != _thread && _thread.IsAlive)
        {
            _thread.Join(500);
        }

        _signal.Dispose();
    }

    private void Run()
    {
        while (_running)
        {
            _signal.WaitOne(50);
            if (!_running)
            {
                break;
            }

            Drain();
        }
    }

    private void Drain()
    {
        while (_running)
        {
            if (Interlocked.Exchange(ref _resetRequested, 0) == 1)
            {
                _ring.Clear();
                lock (_trackerLock)
                {
                    _tracker.Reset();
                }

                Volatile.Write(ref _reading, null);
                continue;
            }

            int floats = _ring.Read(_scratch);
            if (floats == 0)
            {
                break;
            }

            bool valid;
            float hz;
            float confidence;
            lock (_trackerLock)
            {
                _tracker.Add(_scratch.AsSpan(0, floats), floats / 2);
                valid = _tracker.TryGetPitch(out hz);
                confidence = _tracker.Confidence;
            }

            Publish(valid, hz, confidence);
        }
    }

    private void Publish(bool valid, float hz, float confidence)
    {
        long now = Environment.TickCount64;
        PitchReading? previous = Volatile.Read(ref _reading);
        bool changed = previous == null ||
                       previous.Valid != valid ||
                       Math.Abs(previous.Hz - hz) > 0.25f ||
                       now - _publishedAt >= 100;
        if (!changed)
        {
            return;
        }

        _publishedAt = now;
        Volatile.Write(ref _reading, new PitchReading(valid, hz, confidence, now));
    }

    private sealed class PitchReading(bool valid, float hz, float confidence, long at)
    {
        public readonly bool Valid = valid;
        public readonly float Hz = hz;
        public readonly float Confidence = confidence;
        public readonly long At = at;
    }
}
