using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Ruse.App.Audio;

/// <summary>
/// Minimal Ruse-to-Ruse PCM transport. Packets are 48 kHz stereo signed
/// 16-bit little-endian with a small sequence/timestamp header. This avoids
/// codec and RTP dependencies while keeping the receive path allocation-light.
/// </summary>
public static class NetworkPcm
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BlockFrames = 240;
    public const int HeaderBytes = 24;
    public const int DefaultPort = 7890;
    public const int DefaultBufferMs = 40;

    public static string GetLocalAddress()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var address in network.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address.Address) &&
                    !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    return address.Address.ToString();
                }
            }
        }

        return "127.0.0.1";
    }
}

/// <summary>Sends interleaved stereo float blocks as UDP PCM packets.</summary>
public sealed class NetworkPcmSender : IDisposable
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _target;
    private readonly byte[] _packet = new byte[NetworkPcm.HeaderBytes + NetworkPcm.BlockFrames * NetworkPcm.Channels * 2];
    private uint _sequence;
    private long _frames;

    public NetworkPcmSender(string host, int port)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            address = Dns.GetHostAddresses(host).FirstOrDefault()
                ?? throw new InvalidOperationException($"Could not resolve '{host}'.");
        }

        _target = new IPEndPoint(address, Math.Clamp(port, 1, 65535));
        _client = new UdpClient(address.AddressFamily);
        _client.Client.SendBufferSize = 1 << 20;
    }

    public string? LastError { get; private set; }

    public void Send(float[] samples, int count)
    {
        int frames = Math.Clamp(count / NetworkPcm.Channels, 0, NetworkPcm.BlockFrames);
        if (frames <= 0)
        {
            return;
        }

        int bytes = NetworkPcm.HeaderBytes + frames * NetworkPcm.Channels * 2;
        _packet[0] = (byte)'R';
        _packet[1] = (byte)'U';
        _packet[2] = (byte)'S';
        _packet[3] = (byte)'E';
        _packet[4] = 1;
        _packet[5] = NetworkPcm.Channels;
        _packet[6] = 0;
        _packet[7] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(_packet.AsSpan(8), NetworkPcm.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(_packet.AsSpan(12), _sequence++);
        BinaryPrimitives.WriteInt64LittleEndian(_packet.AsSpan(16), _frames);

        int offset = NetworkPcm.HeaderBytes;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = frame * NetworkPcm.Channels;
            short left = ToPcm16(samples[index]);
            short right = ToPcm16(samples[index + 1]);
            BinaryPrimitives.WriteInt16LittleEndian(_packet.AsSpan(offset), left);
            BinaryPrimitives.WriteInt16LittleEndian(_packet.AsSpan(offset + 2), right);
            offset += 4;
        }

        _frames += frames;
        try
        {
            _client.Send(_packet, bytes, _target);
            LastError = null;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            LastError = ex.Message;
        }
    }

    private static short ToPcm16(float sample)
    {
        float value = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
        return (short)MathF.Round(value * short.MaxValue);
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Receives UDP PCM, reorders packets, and exposes a real-time stereo stream.</summary>
public sealed class NetworkPcmCapture : IDisposable
{
    private readonly int _port;
    private readonly int _targetBufferMs;
    private readonly FloatRingBuffer _ring = new(NetworkPcm.SampleRate * NetworkPcm.Channels * 2);
    private readonly float[] _samples = new float[NetworkPcm.BlockFrames * NetworkPcm.Channels];
    private readonly float[] _decode = new float[NetworkPcm.BlockFrames * NetworkPcm.Channels];
    private readonly byte[] _bytes = new byte[NetworkPcm.BlockFrames * NetworkPcm.Channels * sizeof(float)];
    private readonly AudioPacketEventArgs _packetArgs = new();
    private readonly object _errorLock = new();

    private UdpClient? _client;
    private Thread? _receiver;
    private Thread? _pump;
    private volatile bool _stop;
    private long _lastSequence = -1;
    private bool _primed;
    private string? _lastError;
    private long _packets;
    private long _lostPackets;
    private long _underruns;

    public NetworkPcmCapture(int port, int targetBufferMs = NetworkPcm.DefaultBufferMs)
    {
        _port = Math.Clamp(port, 1, 65535);
        _targetBufferMs = Math.Clamp(targetBufferMs, 10, 250);
    }

    public event EventHandler<AudioPacketEventArgs>? DataAvailable;
    public bool IsRunning => _receiver is { IsAlive: true } && !_stop;
    public string? LastError { get { lock (_errorLock) return _lastError; } }
    public long Packets => Interlocked.Read(ref _packets);
    public long LostPackets => Interlocked.Read(ref _lostPackets);
    public long Underruns => Interlocked.Read(ref _underruns);
    public double BufferedMs => _ring.AvailableToRead / 2.0 * 1000.0 / NetworkPcm.SampleRate;

    public void Start()
    {
        if (_receiver != null)
        {
            return;
        }

        _stop = false;
        _primed = false;
        _lastSequence = -1;
        _ring.Clear();
        var client = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        client.Client.ReceiveBufferSize = 1 << 20;
        TryDisableConnectionReset(client);
        _client = client;
        _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = $"NetworkInput-{_port}" };
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = $"NetworkPump-{_port}" };
        _receiver.Start();
        _pump.Start();
    }

    private void ReceiveLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop)
        {
            try
            {
                byte[] packet = _client!.Receive(ref remote);
                Parse(packet);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (!_stop)
                {
                    SetError(ex.Message);
                    Thread.Sleep(5);
                }
            }
            catch (Exception ex)
            {
                if (!_stop)
                {
                    SetError(ex.Message);
                    Thread.Sleep(5);
                }
            }
        }
    }

    private void Parse(byte[] packet)
    {
        if (packet.Length < NetworkPcm.HeaderBytes + 4 ||
            packet[0] != 'R' || packet[1] != 'U' || packet[2] != 'S' || packet[3] != 'E' ||
            packet[4] != 1 ||
            packet[5] != NetworkPcm.Channels ||
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) != NetworkPcm.SampleRate)
        {
            return;
        }

        int frames = (packet.Length - NetworkPcm.HeaderBytes) / (NetworkPcm.Channels * 2);
        if (frames <= 0 || frames > NetworkPcm.BlockFrames)
        {
            return;
        }

        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12));
        if (_lastSequence >= 0 && sequence <= (uint)_lastSequence)
        {
            return;
        }

        if (_lastSequence >= 0)
        {
            long missing = (long)sequence - _lastSequence - 1;
            if (missing > 0)
            {
                int missingFrames = (int)Math.Min(missing * frames, NetworkPcm.SampleRate / 2);
                WriteSilence(missingFrames);
                Interlocked.Add(ref _lostPackets, missing);
            }
        }

        _lastSequence = sequence;
        int offset = NetworkPcm.HeaderBytes;
        for (int frame = 0; frame < frames; frame++)
        {
            short left = BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(offset));
            short right = BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(offset + 2));
            _decode[frame * 2] = left / 32768f;
            _decode[frame * 2 + 1] = right / 32768f;
            offset += 4;
        }

        _ring.Write(_decode.AsSpan(0, frames * NetworkPcm.Channels));
        Interlocked.Increment(ref _packets);
    }

    private void WriteSilence(int frames)
    {
        var silence = new float[frames * NetworkPcm.Channels];
        _ring.Write(silence);
    }

    private void PumpLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long produced = 0;
        int targetFrames = NetworkPcm.SampleRate * _targetBufferMs / 1000;
        int maxFrames = NetworkPcm.SampleRate / 2;
        while (!_stop)
        {
            long due = (long)(clock.Elapsed.TotalSeconds * NetworkPcm.SampleRate);
            if (produced >= due)
            {
                Thread.Sleep(1);
                continue;
            }

            if (!_primed)
            {
                if (_ring.AvailableToRead / 2 < targetFrames)
                {
                    Array.Clear(_samples);
                    Emit(_samples, NetworkPcm.BlockFrames);
                    produced += NetworkPcm.BlockFrames;
                    continue;
                }

                _primed = true;
            }

            int available = _ring.AvailableToRead / 2;
            if (available > maxFrames)
            {
                _ring.Skip((available - targetFrames) * NetworkPcm.Channels);
            }

            int read = _ring.Read(_samples);
            int framesRead = read / NetworkPcm.Channels;
            if (framesRead < NetworkPcm.BlockFrames)
            {
                Array.Clear(_samples, read, _samples.Length - read);
                Interlocked.Increment(ref _underruns);
            }

            Emit(_samples, NetworkPcm.BlockFrames);
            produced += NetworkPcm.BlockFrames;
        }
    }

    private void Emit(float[] samples, int frames)
    {
        Buffer.BlockCopy(samples, 0, _bytes, 0, frames * NetworkPcm.Channels * sizeof(float));
        _packetArgs.Set(_bytes, frames * NetworkPcm.Channels * sizeof(float));
        DataAvailable?.Invoke(this, _packetArgs);
    }

    private void SetError(string message)
    {
        lock (_errorLock)
        {
            _lastError = message;
        }
    }

    private static void TryDisableConnectionReset(UdpClient client)
    {
        try
        {
            const int SioUdpConnReset = -1744830452;
            client.Client.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch
        {
        }
    }

    public void Stop()
    {
        _stop = true;
        try
        {
            _client?.Dispose();
        }
        catch
        {
        }

        _client = null;
        _receiver?.Join(500);
        _pump?.Join(500);
        _receiver = null;
        _pump = null;
        _primed = false;
    }

    public void Dispose() => Stop();
}
