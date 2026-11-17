using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    private readonly ushort _session = (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);
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
        // The engine block can be any size (2.5-10 ms). Split it into packets
        // instead of clamping: clamping silently dropped everything past the
        // first 5 ms, which made any larger block sound like constant dropouts.
        int totalFrames = Math.Min(count / NetworkPcm.Channels, samples.Length / NetworkPcm.Channels);
        for (int frameOffset = 0; frameOffset < totalFrames; frameOffset += NetworkPcm.BlockFrames)
        {
            SendPacket(samples, frameOffset, Math.Min(NetworkPcm.BlockFrames, totalFrames - frameOffset));
        }
    }

    private void SendPacket(float[] samples, int frameOffset, int frames)
    {
        int bytes = NetworkPcm.HeaderBytes + frames * NetworkPcm.Channels * 2;
        _packet[0] = (byte)'R';
        _packet[1] = (byte)'U';
        _packet[2] = (byte)'S';
        _packet[3] = (byte)'E';
        _packet[4] = 1;
        _packet[5] = NetworkPcm.Channels;
        // A fresh sender (for example, a network output that was removed and
        // added again) restarts at sequence 0. The session byte lets the
        // receiver tell that apart from a late packet.
        BinaryPrimitives.WriteUInt16LittleEndian(_packet.AsSpan(6), _session);
        BinaryPrimitives.WriteInt32LittleEndian(_packet.AsSpan(8), NetworkPcm.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(_packet.AsSpan(12), _sequence++);
        BinaryPrimitives.WriteInt64LittleEndian(_packet.AsSpan(16), _frames);

        int offset = NetworkPcm.HeaderBytes;
        for (int frame = 0; frame < frames; frame++)
        {
            int index = (frameOffset + frame) * NetworkPcm.Channels;
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
    private int _targetBufferMs;
    private readonly FloatRingBuffer _ring = new(NetworkPcm.SampleRate * NetworkPcm.Channels * 2);
    private readonly float[] _samples = new float[NetworkPcm.BlockFrames * NetworkPcm.Channels];
    private readonly float[] _decode = new float[NetworkPcm.BlockFrames * NetworkPcm.Channels];
    private readonly byte[] _bytes = new byte[NetworkPcm.BlockFrames * NetworkPcm.Channels * sizeof(float)];
    private readonly float[] _silence = new float[NetworkPcm.SampleRate / 2 * NetworkPcm.Channels];
    private readonly AudioPacketEventArgs _packetArgs = new();
    private readonly object _errorLock = new();

    private UdpClient? _client;
    private readonly byte[] _receiveBuffer = new byte[65_536];
    private const int ReorderSlots = 32;
    private const int ReorderPacketBytes = NetworkPcm.HeaderBytes + NetworkPcm.BlockFrames * NetworkPcm.Channels * 2;
    private readonly byte[] _reorderBuffer = new byte[ReorderSlots * ReorderPacketBytes];
    private readonly int[] _reorderLengths = new int[ReorderSlots];
    private readonly long[] _reorderSequences = new long[ReorderSlots];
    private readonly long[] _reorderTimestamps = new long[ReorderSlots];
    private readonly bool[] _reorderReady = new bool[ReorderSlots];
    private long _expectedSequence = -1;
    private long _expectedTimestamp = -1;
    private long _reorderDeadline;
    private long _lastPacketTick;
    private Thread? _receiver;
    private Thread? _pump;
    private volatile bool _stop;
    private int _lastPacketFrames = NetworkPcm.BlockFrames;
    private int _session = -1;
    private volatile bool _primed;
    private string? _lastError;
    private long _packets;
    private long _lostPackets;
    private long _underruns;
    private long _streamResets;
    private long _lastUnderrunMark;
    private long _lastBufferAdjustTick;
    private AudioWatchdogToken? _watchdog;

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
    public long StreamResets => Interlocked.Read(ref _streamResets);
    public double BufferedMs => _ring.AvailableToRead / 2.0 * 1000.0 / NetworkPcm.SampleRate;

    public void Start()
    {
        if (_receiver is { IsAlive: true })
        {
            return;
        }

        _receiver = null;
        _pump = null;
        _stop = false;
        _primed = false;
        _targetBufferMs = Math.Clamp(_targetBufferMs, 10, 250);
        _lastUnderrunMark = 0;
        _lastBufferAdjustTick = Environment.TickCount64;
        _session = -1;
        ResetReorder();
        Interlocked.Exchange(ref _streamResets, 0);
        _ring.Clear();
        var client = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        client.Client.ReceiveBufferSize = 1 << 20;
        client.Client.ReceiveTimeout = 20;
        client.Client.Blocking = false;
        TryDisableConnectionReset(client);
        _client = client;
        _watchdog = AudioThreadWatchdog.Register($"NetworkInput-{_port}", TimeSpan.FromSeconds(2), () =>
        {
            SetError("Network input stopped responding; closing the socket to recover.");
            try { _client?.Dispose(); } catch { }
        });
        _receiver = new Thread(() => ReceiveLoop(client)) { IsBackground = true, Name = $"NetworkInput-{_port}" };
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = $"NetworkPump-{_port}" };
        _receiver.Start();
        _pump.Start();
    }

    private void ReceiveLoop(UdpClient client)
    {
        using var mmcss = MmcssScope.Enter();
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!_stop)
            {
                _watchdog?.Beat();
                try
                {
                    // Poll first so a wedged driver cannot block ReceiveFrom
                    // indefinitely. The socket is non-blocking as well, so a
                    // packet that disappears between Poll and ReceiveFrom
                    // surfaces as WouldBlock instead of a hang.
                    if (!client.Client.Poll(20_000, SelectMode.SelectRead))
                    {
                        FlushReorder(Environment.TickCount64);
                        continue;
                    }

                    int received = client.Client.ReceiveFrom(_receiveBuffer, ref remote);
                    if (received > 0)
                    {
                        Parse(_receiveBuffer, received);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    if (_stop) break;
                    if (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
                    {
                        FlushReorder(Environment.TickCount64);
                    }
                    else
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
        finally
        {
            try { client.Dispose(); } catch { }
        }
    }

    private void Parse(byte[] packet, int length)
    {
        if (length < NetworkPcm.HeaderBytes + 4 ||
            packet[0] != 'R' || packet[1] != 'U' || packet[2] != 'S' || packet[3] != 'E' ||
            packet[4] != 1 ||
            packet[5] != NetworkPcm.Channels ||
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) != NetworkPcm.SampleRate)
        {
            return;
        }

        int frames = (length - NetworkPcm.HeaderBytes) / (NetworkPcm.Channels * 2);
        if (frames <= 0 || frames > NetworkPcm.BlockFrames)
        {
            return;
        }

        ushort session = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6));
        if (session != _session)
        {
            if (_session != -1)
            {
                ResetStream();
            }

            _session = session;
        }

        long sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12));
        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(16));
        long now = Environment.TickCount64;
        _lastPacketTick = now;
        _lastPacketFrames = frames;

        if (_expectedSequence < 0)
        {
            _expectedSequence = sequence;
            _expectedTimestamp = timestamp;
        }
        else if (sequence < _expectedSequence)
        {
            if (_expectedSequence - sequence > 64)
            {
                ResetStream();
                _expectedSequence = sequence;
                _expectedTimestamp = timestamp;
            }
            else
            {
                return;
            }
        }
        else if (sequence >= _expectedSequence + ReorderSlots)
        {
            int missing = (int)Math.Clamp(timestamp - _expectedTimestamp, 0, NetworkPcm.SampleRate / 2);
            WriteSilence(missing);
            Interlocked.Add(ref _lostPackets, Math.Max(0, sequence - _expectedSequence));
            _expectedSequence = sequence;
            _expectedTimestamp = timestamp;
        }

        int slot = (int)(sequence % ReorderSlots);
        if (_reorderReady[slot] && _reorderSequences[slot] == sequence)
        {
            return;
        }

        packet.AsSpan(0, length).CopyTo(_reorderBuffer.AsSpan(slot * ReorderPacketBytes, length));
        _reorderLengths[slot] = length;
        _reorderSequences[slot] = sequence;
        _reorderTimestamps[slot] = timestamp;
        _reorderReady[slot] = true;
        if (sequence != _expectedSequence && _reorderDeadline == 0)
        {
            _reorderDeadline = now + 20;
        }

        FlushReorder(now);
    }

    private void FlushReorder(long now)
    {
        while (_expectedSequence >= 0)
        {
            int slot = (int)(_expectedSequence % ReorderSlots);
            if (_reorderReady[slot] && _reorderSequences[slot] == _expectedSequence)
            {
                int length = _reorderLengths[slot];
                int frames = (length - NetworkPcm.HeaderBytes) / (NetworkPcm.Channels * 2);
                int offset = slot * ReorderPacketBytes + NetworkPcm.HeaderBytes;
                for (int frame = 0; frame < frames; frame++)
                {
                    short left = BinaryPrimitives.ReadInt16LittleEndian(
                        _reorderBuffer.AsSpan(offset));
                    short right = BinaryPrimitives.ReadInt16LittleEndian(
                        _reorderBuffer.AsSpan(offset + 2));
                    _decode[frame * 2] = left / 32768f;
                    _decode[frame * 2 + 1] = right / 32768f;
                    offset += 4;
                }

                _ring.Write(_decode.AsSpan(0, frames * NetworkPcm.Channels));
                Interlocked.Increment(ref _packets);
                _expectedTimestamp = _reorderTimestamps[slot] + frames;
                _expectedSequence++;
                _reorderReady[slot] = false;
                _reorderDeadline = 0;
                continue;
            }

            if (_reorderDeadline == 0)
            {
                _reorderDeadline = now + 20;
            }

            if (now < _reorderDeadline)
            {
                return;
            }

            if (now - _lastPacketTick > 500)
            {
                ResetReorder();
                return;
            }

            long nextTimestamp = -1;
            for (int i = 1; i < ReorderSlots; i++)
            {
                int candidate = (int)((_expectedSequence + i) % ReorderSlots);
                if (_reorderReady[candidate] &&
                    _reorderSequences[candidate] > _expectedSequence)
                {
                    nextTimestamp = _reorderTimestamps[candidate];
                    break;
                }
            }

            if (nextTimestamp < 0)
            {
                _reorderDeadline = now + 20;
                return;
            }

            int missingFrames = _lastPacketFrames;
            if (nextTimestamp >= _expectedTimestamp)
            {
                missingFrames = (int)Math.Clamp(
                    nextTimestamp - _expectedTimestamp, 0, NetworkPcm.SampleRate / 2);
            }

            WriteSilence(missingFrames);
            Interlocked.Increment(ref _lostPackets);
            _expectedSequence++;
            _expectedTimestamp += missingFrames;
            _reorderDeadline = 0;
        }
    }

    private void ResetReorder()
    {
        Array.Clear(_reorderReady);
        _expectedSequence = -1;
        _expectedTimestamp = -1;
        _reorderDeadline = 0;
    }

    private void ResetStream()
    {
        _primed = false;
        ResetReorder();
        Interlocked.Increment(ref _streamResets);
    }

    private void WriteSilence(int frames)
    {
        int samples = Math.Clamp(frames, 0, NetworkPcm.SampleRate / 2) * NetworkPcm.Channels;
        if (samples > 0)
        {
            _ring.Write(_silence.AsSpan(0, samples));
        }
    }

    private void AdaptJitterBuffer()
    {
        long now = Environment.TickCount64;
        long underruns = Interlocked.Read(ref _underruns);
        if (underruns > _lastUnderrunMark)
        {
            _lastUnderrunMark = underruns;
            if (now - _lastBufferAdjustTick >= 1000)
            {
                _targetBufferMs = Math.Min(250, _targetBufferMs + 10);
                _lastBufferAdjustTick = now;
            }
        }
        else if (_targetBufferMs > NetworkPcm.DefaultBufferMs &&
                 now - _lastBufferAdjustTick >= 10_000)
        {
            _targetBufferMs = Math.Max(NetworkPcm.DefaultBufferMs, _targetBufferMs - 5);
            _lastBufferAdjustTick = now;
        }
    }

    private void PumpLoop()
    {
        using var mmcss = MmcssScope.Enter();
        using var timer = HighResolutionTimer.TryCreate();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long produced = 0;
        int maxFrames = NetworkPcm.SampleRate / 2;
        while (!_stop)
        {
            _watchdog?.Beat();
            AdaptJitterBuffer();
            int targetFrames = NetworkPcm.SampleRate * _targetBufferMs / 1000;
            long due = (long)(clock.Elapsed.TotalSeconds * NetworkPcm.SampleRate);
            if (produced >= due)
            {
                WaitForNextBlock(clock, produced, timer);
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

    private void WaitForNextBlock(
        System.Diagnostics.Stopwatch clock, long producedFrames, HighResolutionTimer? timer)
    {
        long dueTicks = (long)(producedFrames * (double)System.Diagnostics.Stopwatch.Frequency /
            NetworkPcm.SampleRate);
        long sliceTicks = System.Diagnostics.Stopwatch.Frequency / 50; // 20 ms stop latency
        while (!_stop)
        {
            long remaining = dueTicks - clock.ElapsedTicks;
            if (remaining <= 0)
            {
                return;
            }

            long waitTicks = Math.Min(remaining, sliceTicks);
            if (timer != null && timer.Wait(waitTicks))
            {
                continue;
            }

            // Fallback for systems without a high-resolution waitable timer.
            // Sleep is only a coarse lower bound, so the caller re-checks the
            // clock and the stop flag.
            double milliseconds = waitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (milliseconds > 1.6)
            {
                Thread.Sleep((int)(milliseconds - 1));
            }
            else
            {
                Thread.SpinWait(200);
            }
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

    public bool Stop()
    {
        _stop = true;
        _watchdog?.Dispose();
        _watchdog = null;

        // Null the field before disposing so a concurrent Start cannot hand the
        // same socket to a new receiver thread.
        UdpClient? client = Interlocked.Exchange(ref _client, null);
        try
        {
            client?.Dispose();
        }
        catch
        {
        }

        bool receiverJoined = _receiver == null || _receiver == Thread.CurrentThread ||
            _receiver.Join(500);
        bool pumpJoined = _pump == null || _pump == Thread.CurrentThread ||
            _pump.Join(500);

        if (receiverJoined)
        {
            _receiver = null;
        }

        if (pumpJoined)
        {
            _pump = null;
        }

        _primed = false;

        if (!receiverJoined || !pumpJoined)
        {
            Ruse.App.Diagnostics.AppLog.Warning(
                $"Network input '{_port}' threads did not stop in time; they will exit in the background.");
            return false;
        }

        return true;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// A waitable timer created with the high-resolution flag. It wakes the audio
/// pump close to the requested deadline instead of the 15.6 ms Sleep quantum,
/// which otherwise makes the pump drain several jitter-buffer blocks in one
/// burst and then underrun. Falls back to Sleep when the OS does not support it.
/// </summary>
internal sealed class HighResolutionTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;

    private readonly SafeWaitHandle? _handle;

    private HighResolutionTimer(SafeWaitHandle? handle) => _handle = handle;

    public static HighResolutionTimer? TryCreate()
    {
        try
        {
            IntPtr handle = CreateWaitableTimerExW(
                IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            return handle == IntPtr.Zero ? null : new HighResolutionTimer(new SafeWaitHandle(handle, true));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Waits until <paramref name="remainingTicks"/> Stopwatch ticks have elapsed.</summary>
    public bool Wait(long remainingTicks)
    {
        if (_handle is null || _handle.IsInvalid || remainingTicks <= 0)
        {
            return false;
        }

        // SetWaitableTimer uses 100 ns units; negative values are relative.
        long due = -(remainingTicks * 10_000_000L / System.Diagnostics.Stopwatch.Frequency);
        if (due >= 0)
        {
            due = -1;
        }

        if (!SetWaitableTimer(_handle.DangerousGetHandle(), ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            return false;
        }

        // A finite timeout keeps the thread from hanging if the timer is
        // cancelled or the handle becomes invalid during shutdown.
        double milliseconds = remainingTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        uint timeout = (uint)Math.Clamp(Math.Ceiling(milliseconds) + 5, 1, 1000);
        WaitForSingleObject(_handle.DangerousGetHandle(), timeout);
        return true;
    }

    public void Dispose() => _handle?.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        IntPtr hTimer, ref long lpDueTime, int lPeriod,
        IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}
