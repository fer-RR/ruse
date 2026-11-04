using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>
/// Plays a queue of generated TTS WAV files as one continuous source. The next
/// sentence can be appended while the current one is still playing; file
/// boundaries are crossed inside a block so there is no deliberate gap.
/// </summary>
public sealed class TtsAudioCapture : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<string> _files = new();
    private readonly WaveFormat _format;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _framesPerBlock;
    private readonly float[] _samples;
    private readonly byte[] _bytes;
    private readonly AudioPacketEventArgs _packetArgs = new();

    private AudioFileReader? _reader;
    private string? _currentPath;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _complete;
    private volatile bool _ended;

    public TtsAudioCapture(string firstPath)
    {
        _reader = new AudioFileReader(firstPath);
        _currentPath = firstPath;
        _format = _reader.WaveFormat;
        _channels = Math.Max(1, _format.Channels);
        _sampleRate = _format.SampleRate;
        _framesPerBlock = Math.Max(1, _sampleRate * 5 / 1000);
        _samples = new float[_framesPerBlock * _channels];
        _bytes = new byte[_samples.Length * sizeof(float)];
    }

    public event EventHandler<AudioPacketEventArgs>? DataAvailable;

    public WaveFormat WaveFormat => _format;
    public bool IsRunning => _thread is { IsAlive: true } && !_ended;
    public int QueuedFiles { get { lock (_sync) return _files.Count; } }

    public void Enqueue(string path)
    {
        lock (_sync)
        {
            _files.Enqueue(path);
        }
    }

    public void Complete() => _complete = true;

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _stop = false;
        _ended = false;
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "TtsAudioPump",
        };
        _thread.Start();
    }

    private void Pump()
    {
        using var mmcss = MmcssScope.Enter();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long produced = 0;
        while (!_stop)
        {
            long due = (long)(clock.Elapsed.TotalSeconds * _sampleRate);
            if (produced >= due)
            {
                Thread.Sleep(1);
                continue;
            }

            int frames = (int)Math.Min(_framesPerBlock, Math.Max(1, due - produced));
            int samples = frames * _channels;
            int offset = 0;
            while (offset < samples && !_stop)
            {
                if (_reader == null && !TryOpenNext())
                {
                    if (_complete)
                    {
                        _ended = true;
                        _thread = null;
                        return;
                    }

                    // The next sentence is still being generated. Keep the
                    // source alive and let the engine underrun rather than
                    // tearing down the node.
                    Thread.Sleep(5);
                    continue;
                }

                int read = _reader!.Read(_samples.AsSpan(offset, samples - offset));
                if (read <= 0)
                {
                    CloseCurrent();
                    continue;
                }

                offset += read;
            }

            if (offset < samples)
            {
                Array.Clear(_samples, offset, samples - offset);
            }

            Buffer.BlockCopy(_samples, 0, _bytes, 0, samples * sizeof(float));
            _packetArgs.Set(_bytes, samples * sizeof(float));
            DataAvailable?.Invoke(this, _packetArgs);
            produced += frames;
        }
    }

    private bool TryOpenNext()
    {
        string? path;
        lock (_sync)
        {
            if (_files.Count == 0)
            {
                return false;
            }

            path = _files.Dequeue();
        }

        try
        {
            var reader = new AudioFileReader(path);
            if (reader.WaveFormat.SampleRate != _sampleRate || reader.WaveFormat.Channels != _channels)
            {
                reader.Dispose();
                TryDelete(path);
                return false;
            }

            _reader = reader;
            _currentPath = path;
            return true;
        }
        catch
        {
            TryDelete(path);
            return false;
        }
    }

    private void CloseCurrent()
    {
        string? path = _currentPath;
        _reader?.Dispose();
        _reader = null;
        _currentPath = null;
        TryDelete(path);
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    public void Stop()
    {
        _stop = true;
        string? current = _currentPath;
        _reader?.Dispose();
        _reader = null;
        _currentPath = null;
        TryDelete(current);
        _thread?.Join(500);
        _thread = null;
        _ended = true;
        lock (_sync)
        {
            while (_files.Count > 0)
            {
                TryDelete(_files.Dequeue());
            }
        }
    }

    public void Dispose()
    {
        Stop();
        TryDelete(_currentPath);
    }
}
