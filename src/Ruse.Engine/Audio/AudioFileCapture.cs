using NAudio.Effects;
using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>Plays an audio file into the mixer at real time, like a capture source.</summary>
public sealed class AudioFileCapture : IDisposable
{
    private readonly AudioFileReader _reader;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _framesPerBlock;
    private readonly float[] _samples;
    private readonly byte[] _bytes;
    private readonly AudioPacketEventArgs _packetArgs = new();
    private readonly float[] _lastSamples;
    private readonly float[] _speedSamples;
    private readonly PitchShiftEffect _pitchShift;
    private int _fadeRemaining;
    private int _fadeTotal;

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _ended;
    private volatile bool _restart;
    private volatile bool _paused;
    private volatile int _seekRequest = -1;
    private volatile float _positionSeconds;
    private volatile float _speed = 1f;

    public AudioFileCapture(string path, bool loop, float speed = 1f)
    {
        FilePath = path;
        Loop = loop;
        Speed = speed;
        _reader = new AudioFileReader(path);
        _channels = Math.Max(1, _reader.WaveFormat.Channels);
        _sampleRate = _reader.WaveFormat.SampleRate;
        _framesPerBlock = Math.Max(1, _sampleRate * 5 / 1000);
        _samples = new float[_framesPerBlock * _channels];
        _lastSamples = new float[_channels];
        _speedSamples = new float[(_framesPerBlock * 2 + 2) * _channels];
        _pitchShift = new PitchShiftEffect
        {
            FftSize = 2048,
            Oversampling = 4,
            Mix = 1f,
        };
        _pitchShift.Configure(WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels));
        _bytes = new byte[_samples.Length * sizeof(float)];
        DurationSeconds = _reader.TotalTime.TotalSeconds;
    }

    public string FilePath { get; }
    public WaveFormat WaveFormat => _reader.WaveFormat;
    public double DurationSeconds { get; }
    public float PositionSeconds => _positionSeconds;
    public bool IsRunning => _thread is { IsAlive: true } && !_ended;
    public bool IsPaused => _paused;
    public float Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.5f, 2f);
    }
    public volatile bool Loop;

    public static double GetDuration(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            return reader.TotalTime.TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }

    public event EventHandler<AudioPacketEventArgs>? DataAvailable;

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _stop = false;
        _ended = false;
        _restart = false;
        _paused = false;
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "AudioFilePump",
        };
        _thread.Start();
    }

    public void Pause()
    {
        if (_thread is { IsAlive: true })
        {
            _paused = true;
        }
    }

    public void Resume()
    {
        _paused = false;
    }

    public void Seek(double seconds)
    {
        _seekRequest = (int)Math.Clamp(seconds * 1000.0, 0, int.MaxValue);
    }

    public void Restart()
    {
        _paused = false;
        if (_thread is { IsAlive: true })
        {
            _restart = true;
            return;
        }

        try
        {
            _reader.Position = 0;
        }
        catch
        {
        }

        Start();
    }

    private void Pump()
    {
        using var mmcss = MmcssScope.Enter();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long produced = 0;

        void ApplySeek()
        {
            int request = _seekRequest;
            if (request < 0)
            {
                return;
            }

            _seekRequest = -1;
            try
            {
                _reader.CurrentTime = TimeSpan.FromSeconds(
                    Math.Clamp(request / 1000.0, 0, Math.Max(0, DurationSeconds)));
                _positionSeconds = (float)_reader.CurrentTime.TotalSeconds;
                BeginFade();
            }
            catch
            {
                // A closed or unsupported stream simply stays at its position.
            }

                produced = 0;
                clock.Restart();
                _ended = false;
                BeginFade();
            }

        while (!_stop)
        {
            if (_paused)
            {
                clock.Stop();
                while (_paused && !_stop)
                {
                    ApplySeek();
                    Thread.Sleep(10);
                }

                if (_stop)
                {
                    return;
                }

                clock.Start();
            }

            ApplySeek();

            if (_restart)
            {
                _restart = false;
                try
                {
                    _reader.Position = 0;
                }
                catch
                {
                    // A closed or unsupported stream simply stays at its position.
                }

                produced = 0;
                clock.Restart();
                _ended = false;
            }

            long due = (long)(clock.Elapsed.TotalSeconds * _sampleRate);
            if (produced >= due)
            {
                Thread.Sleep(1);
                continue;
            }

            int frames = (int)Math.Min(_framesPerBlock, Math.Max(1, due - produced));
            int samples = frames * _channels;
            int read;
            if (Math.Abs(_speed - 1f) < 0.001f)
            {
                read = _reader.Read(_samples.AsSpan(0, samples));
            }
            else
            {
                int sourceFrames = Math.Max(1, (int)Math.Ceiling(frames * _speed));
                int sourceSamples = sourceFrames * _channels;
                int sourceRead = _reader.Read(_speedSamples.AsSpan(0, sourceSamples));
                int sourceFramesRead = sourceRead / _channels;
                if (sourceFramesRead <= 0)
                {
                    read = 0;
                }
                else
                {
                    for (int frame = 0; frame < frames; frame++)
                    {
                        double position = frame * _speed;
                        int index = (int)position;
                        float fraction = (float)(position - index);
                        int a = Math.Min(index, sourceFramesRead - 1);
                        int b = Math.Min(index + 1, sourceFramesRead - 1);
                        int output = frame * _channels;
                        int sampleA = a * _channels;
                        int sampleB = b * _channels;
                        for (int channel = 0; channel < _channels; channel++)
                        {
                            float first = _speedSamples[sampleA + channel];
                            float second = _speedSamples[sampleB + channel];
                            _samples[output + channel] = first + (second - first) * fraction;
                        }
                    }

                    read = samples;
                }
            }

            if (read <= 0)
            {
                if (!Loop)
                {
                    _ended = true;
                    _thread = null;
                    return;
                }

                try
                {
                    _reader.Position = 0;
                    BeginFade();
                }
                catch
                {
                    _ended = true;
                    _thread = null;
                    return;
                }

                produced = 0;
                clock.Restart();
                continue;
            }

            _positionSeconds = (float)_reader.CurrentTime.TotalSeconds;
            if (Math.Abs(_speed - 1f) >= 0.001f)
            {
                _pitchShift.PitchSemitones = (float)(-12.0 * Math.Log2(_speed));
                _pitchShift.Process(_samples.AsSpan(0, read));
            }
            ApplyFade(_samples, read, _channels);
            RememberLastSamples(_samples, read, _channels);
            Buffer.BlockCopy(_samples, 0, _bytes, 0, read * sizeof(float));
            _packetArgs.Set(_bytes, read * sizeof(float));
            DataAvailable?.Invoke(this, _packetArgs);
            produced += read / _channels;
        }
    }

    private void BeginFade()
    {
        _fadeTotal = Math.Max(1, _sampleRate / 200);
        _fadeRemaining = _fadeTotal;
    }

    private void ApplyFade(float[] samples, int sampleCount, int channels)
    {
        if (_fadeRemaining <= 0)
        {
            return;
        }

        int frames = sampleCount / channels;
        int fade = Math.Min(frames, _fadeRemaining);
        for (int frame = 0; frame < fade; frame++)
        {
            float t = 1f - (_fadeRemaining - frame) / (float)_fadeTotal;
            int index = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                samples[index + channel] = _lastSamples[channel] +
                    (samples[index + channel] - _lastSamples[channel]) * t;
            }
        }

        _fadeRemaining -= fade;
    }

    private void RememberLastSamples(float[] samples, int sampleCount, int channels)
    {
        if (sampleCount < channels)
        {
            return;
        }

        int index = sampleCount - channels;
        for (int channel = 0; channel < channels; channel++)
        {
            _lastSamples[channel] = samples[index + channel];
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
    }

    public void Dispose()
    {
        Stop();
        _reader.Dispose();
    }
}
