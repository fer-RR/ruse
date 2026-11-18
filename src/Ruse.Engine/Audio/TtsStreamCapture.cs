using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Real-time 48 kHz stereo float source for streaming TTS. The producer appends
/// interleaved samples; a paced pump emits 5 ms blocks to the graph.
/// </summary>
public sealed class TtsStreamCapture : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int FramesPerBlock = SampleRate * 5 / 1000;
    private const int MaxQueuedFrames = SampleRate * 30;

    private readonly object _sync = new();
    private readonly Queue<float[]> _pending = new();
    private readonly float[] _block = new float[FramesPerBlock * Channels];
    private readonly byte[] _bytes = new byte[FramesPerBlock * Channels * sizeof(float)];
    private readonly AudioPacketEventArgs _packetArgs = new();
    private readonly WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    private Thread? _thread;
    private float[]? _current;
    private int _currentOffset;
    private int _queuedFrames;
    private volatile bool _stop;
    private volatile bool _complete;
    private volatile bool _ended;
    private bool _started;

    public event EventHandler<AudioPacketEventArgs>? DataAvailable;

    public WaveFormat WaveFormat => _format;
    public bool IsRunning => _thread is { IsAlive: true } && !_ended;
    public int QueuedFrames { get { lock (_sync) return _queuedFrames; } }

    public bool Enqueue(ReadOnlySpan<float> samples, CancellationToken token = default)
    {
        if (samples.IsEmpty) return false;
        int frames = samples.Length / Channels;
        if (frames <= 0) return false;
        var copy = samples[..(frames * Channels)].ToArray();
        lock (_sync)
        {
            while (_queuedFrames + frames > MaxQueuedFrames && !_stop && !_ended)
            {
                token.ThrowIfCancellationRequested();
                Monitor.Wait(_sync, 50);
                token.ThrowIfCancellationRequested();
            }

            if (_stop || _ended)
            {
                return false;
            }

            _pending.Enqueue(copy);
            _queuedFrames += frames;
            return true;
        }
    }

    public void Complete()
    {
        _complete = true;
        lock (_sync)
        {
            Monitor.PulseAll(_sync);
        }
    }

    public void Start()
    {
        if (_thread != null) return;
        lock (_sync)
        {
            _pending.Clear();
            _current = null;
            _currentOffset = 0;
            _queuedFrames = 0;
        }

        _stop = false;
        _complete = false;
        _ended = false;
        _started = false;
        _thread = new Thread(Pump) { IsBackground = true, Name = "TtsStreamPump" };
        _thread.Start();
    }

    private void Pump()
    {
        using var mmcss = MmcssScope.Enter();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long produced = 0;
        while (!_stop)
        {
            if (!_started)
            {
                bool ready;
                lock (_sync) ready = _pending.Count > 0 || _current != null || _complete;
                if (!ready)
                {
                    Thread.Sleep(1);
                    continue;
                }

                _started = true;
                clock.Restart();
                produced = 0;
            }

            long due = (long)(clock.Elapsed.TotalSeconds * SampleRate);
            if (produced >= due)
            {
                Thread.Sleep(1);
                continue;
            }

            int frames = (int)Math.Min(FramesPerBlock, Math.Max(1, due - produced));
            int samples = frames * Channels;
            int offset = 0;
            lock (_sync)
            {
                while (offset < samples)
                {
                    if (_current == null || _currentOffset >= _current.Length)
                    {
                        if (_pending.Count == 0) break;
                        _current = _pending.Dequeue();
                        _currentOffset = 0;
                    }

                    int take = Math.Min(_current.Length - _currentOffset, samples - offset);
                    Array.Copy(_current, _currentOffset, _block, offset, take);
                    _currentOffset += take;
                    offset += take;
                    if (_currentOffset >= _current.Length) _current = null;
                }

                _queuedFrames = Math.Max(0, _queuedFrames - (offset / Channels));
                Monitor.PulseAll(_sync);
                if (offset < samples && _complete && _pending.Count == 0 && _current == null)
                {
                    _ended = true;
                    _thread = null;
                    return;
                }
            }

            if (offset < samples) Array.Clear(_block, offset, samples - offset);
            Buffer.BlockCopy(_block, 0, _bytes, 0, samples * sizeof(float));
            _packetArgs.Set(_bytes, samples * sizeof(float));
            DataAvailable?.Invoke(this, _packetArgs);
            produced += frames;
        }
    }

    public void Stop()
    {
        _stop = true;
        Thread? thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            thread.Join(500);
        }

        lock (_sync)
        {
            _pending.Clear();
            _current = null;
            _currentOffset = 0;
            _queuedFrames = 0;
            Monitor.PulseAll(_sync);
        }

        _ended = true;
    }

    public void Dispose() => Stop();
}
