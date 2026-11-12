using NAudio.Wave;

namespace Ruse.App.Audio;

/// <summary>Container and encoding for a recorded mix.</summary>
public enum RecorderMode
{
    Off,
    Armed,
    On,
}

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

    /// <summary>Container/encoder rates exposed by the recorder UI.</summary>
    public static IReadOnlyList<int> SupportedRates(AudioFileFormat format) =>
        format == AudioFileFormat.Mp3
            ? [44_100, 48_000]
            : [44_100, 48_000, 96_000];

    public static bool SupportsSampleRate(AudioFileFormat format, int sampleRate) =>
        SupportedRates(format).Contains(sampleRate);
}

/// <summary>
/// Writes a stereo mix to WAV, MP3 or FLAC at a chosen sample rate. The render
/// thread only copies floats into a lock-free ring; disk I/O, PCM conversion
/// and encoding happen on a worker thread.
/// </summary>
public sealed class AudioFileRecorder : IDisposable
{
    private const int RingSeconds = 10;
    private const int WriterBlockFloats = 8192;

    private readonly string _path;
    private readonly AudioFileFormat _format;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly FloatRingBuffer _samples;
    private readonly object _stopLock = new();

    private WaveFileWriter? _wave;
    private PcmRingProvider? _provider;
    private Thread? _workerThread;
    private long _frames;
    private long _droppedFrames;
    private volatile string? _lastError;
    private volatile bool _completed;
    private volatile bool _stopped;
    private readonly Random _ditherRandom = new();

    public AudioFileRecorder(
        string path, AudioFileFormat format,
        int sampleRate = 48000, int channels = 2)
    {
        _path = path;
        _format = format;
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _channels = Math.Clamp(channels, 1, 2);
        _samples = new FloatRingBuffer(_sampleRate * _channels * RingSeconds);

        if (!IsCompressed)
        {
            _wave = new WaveFileWriter(path, WaveFormatFor(format, _sampleRate, _channels));
        }
        else
        {
            _provider = new PcmRingProvider(
                _samples, () => _completed, _sampleRate, _channels,
                _format == AudioFileFormat.Flac ? 24 : 16);
        }

        _workerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = $"FileWriter-{System.IO.Path.GetFileName(path)}",
        };
        _workerThread.Start();
    }

    public string Path => _path;
    public AudioFileFormat Format => _format;
    public long Frames => Interlocked.Read(ref _frames);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public double Duration => Frames / (double)_sampleRate;
    public string? LastError => _lastError;
    public bool IsCompressed => _format is AudioFileFormat.Mp3 or AudioFileFormat.Flac;

    /// <summary>Accepts one interleaved float block from the render thread.</summary>
    public void Write(float[] samples, int count) =>
        Write(samples.AsSpan(0, count));

    public void Write(ReadOnlySpan<float> samples)
    {
        if (_stopped || samples.IsEmpty || _lastError != null)
        {
            return;
        }

        int written = _samples.Write(samples);
        if (written > 0)
        {
            Interlocked.Add(ref _frames, written / _channels);
        }

        int dropped = samples.Length - written;
        if (dropped > 0)
        {
            Interlocked.Add(ref _droppedFrames, dropped / _channels);
        }
    }

    private void WriteLoop()
    {
        try
        {
            if (IsCompressed)
            {
                EncodeLoop();
            }
            else
            {
                WriteWavLoop();
            }
        }
        catch (Exception ex)
        {
            Fail($"The {AudioFileFormats.Get(_format).DisplayName} writer failed: {ex.Message}");
        }
        finally
        {
            if (!IsCompressed)
            {
                try
                {
                    _wave?.Dispose();
                }
                catch
                {
                }

                _wave = null;
            }
        }
    }

    private void WriteWavLoop()
    {
        var buffer = new float[WriterBlockFloats];
        while (true)
        {
            int read = _samples.Read(buffer);
            if (read > 0)
            {
                if (_format == AudioFileFormat.Wav16)
                {
                    for (int i = 0; i < read; i++)
                    {
                        buffer[i] += (float)((_ditherRandom.NextDouble() - _ditherRandom.NextDouble()) / 32768.0);
                    }
                }

                _wave!.WriteSamples(buffer, 0, read);
                continue;
            }

            if (_completed)
            {
                return;
            }

            Thread.Sleep(1);
        }
    }

    private void EncodeLoop()
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

    private void Fail(string message)
    {
        _lastError ??= message;
        _completed = true;
    }

    public bool Stop(TimeSpan timeout)
    {
        lock (_stopLock)
        {
            if (!_stopped)
            {
                _stopped = true;
                _completed = true;
            }
        }

        Thread? worker = _workerThread;
        if (worker != null && worker != Thread.CurrentThread && !worker.Join(timeout))
        {
            Fail("The audio writer did not finish in time; it will finish in the background.");
            return false;
        }

        _workerThread = null;
        _provider = null;

        if (_lastError == null && DroppedFrames > 0)
        {
            _lastError = $"Recording dropped {DroppedFrames} frames because the writer fell behind.";
        }

        return true;
    }

    public void Stop() => Stop(TimeSpan.FromSeconds(2));

    public void Dispose() => Stop();

    private static WaveFormat WaveFormatFor(
        AudioFileFormat format, int sampleRate, int channels) => format switch
    {
        AudioFileFormat.Wav16 => new WaveFormat(sampleRate, 16, channels),
        AudioFileFormat.Wav24 => new WaveFormat(sampleRate, 24, channels),
        AudioFileFormat.Wav32Float => WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Pulls float samples from the render ring and converts them to PCM on the
    /// encoder thread. No allocations or locks occur on the producer side.
    /// </summary>
    private sealed class PcmRingProvider : IWaveProvider
    {
        private readonly FloatRingBuffer _source;
        private readonly Func<bool> _completed;
        private readonly float[] _floats;
        private readonly byte[] _bytes;
        private readonly int _bytesPerSample;
        private int _offset;
        private int _available;
        private uint _ditherState = 0x9E3779B9;

        public PcmRingProvider(
            FloatRingBuffer source,
            Func<bool> completed,
            int sampleRate,
            int channels,
            int bitsPerSample)
        {
            _source = source;
            _completed = completed;
            _bytesPerSample = Math.Max(2, bitsPerSample / 8);
            _floats = new float[WriterBlockFloats];
            _bytes = new byte[WriterBlockFloats * _bytesPerSample];
            WaveFormat = new WaveFormat(sampleRate, bitsPerSample, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            int written = 0;
            while (written < buffer.Length)
            {
                if (_available == 0)
                {
                    int read = _source.Read(_floats);
                    if (read == 0)
                    {
                        if (_completed())
                        {
                            break;
                        }

                        Thread.Sleep(1);
                        continue;
                    }

                    _offset = 0;
                    _available = read * _bytesPerSample;
                    for (int i = 0; i < read; i++)
                    {
                        if (_bytesPerSample == 2)
                        {
                            // TPDF dither at the 16-bit quantizer step; 24-bit
                            // and float captures stay undithered.
                            float scaled = Math.Clamp(_floats[i], -1f, 1f) * short.MaxValue;
                            short value = (short)Math.Clamp(
                                MathF.Round(scaled + NextTpdf()), -32768f, 32767f);
                            _bytes[i * 2] = (byte)value;
                            _bytes[i * 2 + 1] = (byte)(value >> 8);
                        }
                        else
                        {
                            int value = (int)MathF.Round(Math.Clamp(_floats[i], -1f, 1f) * 8388607f);
                            value = Math.Clamp(value, -8388608, 8388607);
                            int index = i * 3;
                            _bytes[index] = (byte)value;
                            _bytes[index + 1] = (byte)(value >> 8);
                            _bytes[index + 2] = (byte)(value >> 16);
                        }
                    }
                }

                int take = Math.Min(buffer.Length - written, _available);
                _bytes.AsSpan(_offset, take).CopyTo(buffer[written..]);
                _offset += take;
                _available -= take;
                written += take;
            }

            return written;
        }

        private float NextTpdf()
        {
            uint state = _ditherState;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            _ditherState = state;
            float first = (state & 0xFFFF) / 65536f;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            _ditherState = state;
            float second = (state & 0xFFFF) / 65536f;
            return first + second - 1f;
        }
    }}
