using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>Container and encoding for a recorded mix.</summary>
public enum AudioFileFormat
{
    Wav16,
    Wav24,
    Wav32Float,
    Mp3,
    Flac,
}

/// <summary>User-facing name, extension and file-dialog filter for a format.</summary>
public sealed class AudioFileFormatInfo
{
    public required AudioFileFormat Format { get; init; }
    public required string DisplayName { get; init; }
    public required string Extension { get; init; }
    public required string Filter { get; init; }

    public override string ToString() => DisplayName;
}

public static class AudioFileFormats
{
    public static IReadOnlyList<AudioFileFormatInfo> All { get; } = new[]
    {
        new AudioFileFormatInfo
        {
            Format = AudioFileFormat.Wav24,
            DisplayName = "WAV - 24-bit PCM",
            Extension = "wav",
            Filter = "WAV audio (*.wav)|*.wav",
        },
        new AudioFileFormatInfo
        {
            Format = AudioFileFormat.Wav16,
            DisplayName = "WAV - 16-bit PCM",
            Extension = "wav",
            Filter = "WAV audio (*.wav)|*.wav",
        },
        new AudioFileFormatInfo
        {
            Format = AudioFileFormat.Wav32Float,
            DisplayName = "WAV - 32-bit float",
            Extension = "wav",
            Filter = "WAV audio (*.wav)|*.wav",
        },
        new AudioFileFormatInfo
        {
            Format = AudioFileFormat.Mp3,
            DisplayName = "MP3 - 192 kbps",
            Extension = "mp3",
            Filter = "MP3 audio (*.mp3)|*.mp3",
        },
        new AudioFileFormatInfo
        {
            Format = AudioFileFormat.Flac,
            DisplayName = "FLAC - lossless",
            Extension = "flac",
            Filter = "FLAC audio (*.flac)|*.flac",
        },
    };

    public static AudioFileFormatInfo Get(AudioFileFormat format) =>
        All.FirstOrDefault(info => info.Format == format) ?? All[0];

    public static AudioFileFormat Parse(string? name) =>
        Enum.TryParse<AudioFileFormat>(name, ignoreCase: true, out var format) ? format : AudioFileFormat.Wav24;
}

/// <summary>
/// Writes an interleaved 48 kHz stereo float mix to a file. WAV is written as
/// it arrives; MP3 and FLAC are fed to a Media Foundation encoder on a separate
/// thread so the render thread never blocks on the codec.
/// </summary>
public sealed class AudioFileRecorder : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly string _path;
    private readonly AudioFileFormat _format;
    private WaveFileWriter? _wave;
    private Pcm16BlockingProvider? _provider;
    private Thread? _encoderThread;
    private long _frames;
    private volatile string? _lastError;
    private bool _stopped;
    private readonly object _stopLock = new();

    public AudioFileRecorder(string path, AudioFileFormat format)
    {
        _path = path;
        _format = format;

        if (format == AudioFileFormat.Wav16 ||
            format == AudioFileFormat.Wav24 ||
            format == AudioFileFormat.Wav32Float)
        {
            _wave = new WaveFileWriter(path, WaveFormatFor(format));
            return;
        }

        _provider = new Pcm16BlockingProvider();
        _encoderThread = new Thread(EncodeLoop)
        {
            IsBackground = true,
            Name = $"FileEncoder-{System.IO.Path.GetFileName(path)}",
        };
        _encoderThread.Start();
    }

    public string Path => _path;
    public AudioFileFormat Format => _format;
    public long Frames => Interlocked.Read(ref _frames);
    public double Duration => Frames / (double)SampleRate;
    public string? LastError => _lastError;
    public bool IsCompressed => _provider != null;

    /// <summary>Accepts one interleaved float block from the render thread.</summary>
    public void Write(float[] samples, int count)
    {
        if (_stopped || count <= 0 || _lastError != null)
        {
            return;
        }

        try
        {
            if (_wave != null)
            {
                _wave.WriteSamples(samples, 0, count);
            }
            else
            {
                _provider?.Add(samples, count);
            }
        }
        catch (Exception ex)
        {
            Fail($"Could not write the recording: {ex.Message}");
            return;
        }

        Interlocked.Add(ref _frames, count / Channels);
    }

    private void EncodeLoop()
    {
        try
        {
            if (_format == AudioFileFormat.Mp3)
            {
                MediaFoundationEncoder.EncodeToMp3(_provider!, _path, 192000);
            }
            else
            {
                MediaFoundationEncoder.EncodeToFlac(_provider!, _path);
            }
        }
        catch (Exception ex)
        {
            Fail($"The {AudioFileFormats.Get(_format).DisplayName} encoder failed: {ex.Message}");
        }
    }

    private void Fail(string message)
    {
        _lastError ??= message;
        _provider?.CompleteAdding();
    }

    public void Stop()
    {
        lock (_stopLock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }

        _wave?.Dispose();
        _wave = null;

        if (_provider != null)
        {
            _provider.CompleteAdding();
            if (_encoderThread != null && !_encoderThread.Join(TimeSpan.FromSeconds(30)))
            {
                Fail("The encoder did not finish in time.");
            }

            _encoderThread = null;
            _provider = null;
        }
    }

    public void Dispose() => Stop();

    private static WaveFormat WaveFormatFor(AudioFileFormat format) => format switch
    {
        AudioFileFormat.Wav16 => new WaveFormat(SampleRate, 16, Channels),
        AudioFileFormat.Wav24 => new WaveFormat(SampleRate, 24, Channels),
        AudioFileFormat.Wav32Float => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// A blocking PCM queue for Media Foundation. The batch encoders read until
    /// the provider returns zero; a normal buffered provider would look like
    /// end-of-stream every time it momentarily ran dry.
    /// </summary>
    private sealed class Pcm16BlockingProvider : IWaveProvider
    {
        private readonly object _lock = new();
        private readonly Queue<byte[]> _chunks = new();
        private int _offset;
        private int _available;
        private bool _completed;

        public WaveFormat WaveFormat { get; } = new(SampleRate, 16, Channels);

        public void Add(float[] samples, int count)
        {
            var bytes = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                short value = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                bytes[i * 2] = (byte)value;
                bytes[i * 2 + 1] = (byte)(value >> 8);
            }

            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                _chunks.Enqueue(bytes);
                _available += bytes.Length;
                Monitor.PulseAll(_lock);
            }
        }

        public void CompleteAdding()
        {
            lock (_lock)
            {
                _completed = true;
                Monitor.PulseAll(_lock);
            }
        }

        public int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public int Read(Span<byte> buffer)
        {
            lock (_lock)
            {
                while (!_completed && _available < buffer.Length)
                {
                    Monitor.Wait(_lock);
                }

                if (_available == 0)
                {
                    return 0;
                }

                int written = 0;
                while (written < buffer.Length && _available > 0)
                {
                    var head = _chunks.Peek();
                    int take = Math.Min(buffer.Length - written, head.Length - _offset);
                    head.AsSpan(_offset, take).CopyTo(buffer[written..]);
                    written += take;
                    _offset += take;
                    _available -= take;
                    if (_offset == head.Length)
                    {
                        _chunks.Dequeue();
                        _offset = 0;
                    }
                }

                return written;
            }
        }
    }
}
