using System.IO;
using NAudio.Wave;

namespace Ruse.App.Ui;

/// <summary>
/// Plays short UI feedback sounds (the click when a cable connects).
///
/// The sample is resolved once at startup: a user-supplied
/// <c>Assets\click.wav</c> next to the executable wins, otherwise a subtle
/// click is synthesised so the app never depends on an external asset.
/// Playback happens on a dedicated output device and never blocks the UI.
/// </summary>
internal sealed class UiSoundPlayer : IDisposable
{
    private readonly byte[] _data;
    private readonly int _dataOffset;
    private readonly int _dataLength;
    private readonly WaveFormat _format;

    private readonly object _gate = new();
    private WaveOut? _device;
    private ClickProvider? _provider;
    private bool _broken;

    public UiSoundPlayer()
    {
        byte[] file;
        try
        {
            string custom = Path.Combine(AppContext.BaseDirectory, "Assets", "click.wav");
            file = File.Exists(custom) ? File.ReadAllBytes(custom) : SynthesiseClick();
        }
        catch
        {
            file = SynthesiseClick();
        }

        _data = file;
        (_format, _dataOffset, _dataLength) = ParseWav(file);
    }

    /// <summary>True when a click is available (always true unless the device is broken).</summary>
    public bool Enabled => _dataLength > 0 && !_broken;

    /// <summary>True once the output device has been opened successfully.</summary>
    public bool DeviceReady
    {
        get
        {
            lock (_gate)
            {
                return _device != null;
            }
        }
    }

    /// <summary>
    /// Builds a soft, short tick: a 2.4 kHz sine with a fast exponential decay
    /// plus a one-millisecond noise transient for definition. About 22 ms long
    /// and peaking well below full scale, so it stays unobtrusive.
    /// </summary>
    private static byte[] SynthesiseClick()
    {
        const int sampleRate = 48000;
        const int durationMs = 22;
        int sampleCount = sampleRate * durationMs / 1000;

        var pcm = new byte[sampleCount * 2];
        var random = new Random(20240617);
        double phase = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            double t = i / (double)sampleRate;
            double envelope = Math.Exp(-t / 0.0035);
            double tone = Math.Sin(phase);
            phase += 2 * Math.PI * 2400 / sampleRate;
            double transient = (random.NextDouble() * 2 - 1) * Math.Exp(-t / 0.0006);

            double value = (tone * 0.6 + transient * 0.4) * envelope * 0.32;
            short sample = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return BuildWav(pcm, sampleRate, channels: 1, bitsPerSample: 16);
    }

    private static byte[] BuildWav(byte[] pcm, int sampleRate, short channels, short bitsPerSample)
    {
        short blockAlign = (short)(channels * bitsPerSample / 8);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        writer.Write(36 + pcm.Length);
        writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
        writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>Reads the format and the audio payload out of a RIFF/WAVE file.</summary>
    private static (WaveFormat Format, int DataOffset, int DataLength) ParseWav(byte[] bytes)
    {
        if (bytes.Length < 44 ||
            bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' ||
            bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
        {
            return (new WaveFormat(48000, 16, 1), 0, 0);
        }

        short channels = BitConverter.ToInt16(bytes, 22);
        int sampleRate = BitConverter.ToInt32(bytes, 24);
        short bits = BitConverter.ToInt16(bytes, 34);

        int offset = 12;
        int length = 0;
        while (offset + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            int size = BitConverter.ToInt32(bytes, offset + 4);
            int payload = offset + 8;

            if (id == "data")
            {
                length = Math.Min(size, bytes.Length - payload);
                offset = payload;
                break;
            }

            offset = payload + size + (size % 2);
        }

        var format = new WaveFormat(sampleRate, bits, channels);
        return (format, offset, length);
    }

    /// <summary>Plays the click. Safe to call from any thread; failures are swallowed.</summary>
    public void PlayClick()
    {
        if (_broken || _dataLength == 0)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (_device == null)
                {
                    _device = new WaveOut
                    {
                        BufferMilliseconds = 50,
                        NumberOfBuffers = 3,
                    };
                    _device.PlaybackStopped += (_, e) =>
                    {
                        if (e.Exception != null)
                        {
                            _broken = true;
                        }
                    };

                    _provider = new ClickProvider(_data, _dataOffset, _dataLength, _format);
                    _device.Init(_provider);
                }

                _provider!.Rewind();

                if (_device.PlaybackState == PlaybackState.Playing)
                {
                    // Let the current click finish; the next one starts right after.
                    return;
                }

                _device.Play();
            }
        }
        catch
        {
            // Audio feedback is never important enough to interrupt the app for.
            _broken = true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _device?.Dispose();
            _device = null;
        }
    }

    /// <summary>Feeds the click sample to the output device, once per pass.</summary>
    private sealed class ClickProvider : IWaveProvider
    {
        private readonly byte[] _data;
        private readonly int _offset;
        private readonly int _length;
        private int _position;

        public ClickProvider(byte[] data, int offset, int length, WaveFormat format)
        {
            _data = data;
            _offset = offset;
            _length = length;
            WaveFormat = format;
        }

        public WaveFormat WaveFormat { get; }

        public void Rewind() => _position = 0;

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length)
            {
                return 0;
            }

            int available = Math.Min(count, _length - _position);
            Buffer.BlockCopy(_data, _offset + _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public int Read(Span<byte> buffer)
        {
            if (_position >= _length)
            {
                return 0;
            }

            int available = Math.Min(buffer.Length, _length - _position);
            _data.AsSpan(_offset + _position, available).CopyTo(buffer);
            _position += available;
            return available;
        }
    }
}
