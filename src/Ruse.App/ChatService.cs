using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ruse.App.Audio;

namespace Ruse.App;

/// <summary>
/// Small LAN text chat. It is deliberately separate from the PCM transport:
/// one TCP peer, length-prefixed JSON frames and a short pairing-code
/// challenge. Messages are plain text and are never encrypted.
/// </summary>
internal sealed class ChatService : IDisposable
{
    public const int DefaultPort = NetworkPcm.DefaultChatPort;

    private const int ProtocolVersion = 1;
    private const int MaxFrameBytes = 64 * 1024;
    private const int MaxTextLength = 4000;
    private const int MaxHistory = 200;
    private const int MaxNameLength = 32;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly object _notifyGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly List<ChatMessageDto> _messages = new();

    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _active;
    private bool _hosting;
    private bool _connected;
    private bool _disposed;
    private string _mode = "";
    private string _ownerNodeId = "";
    private string _localName = "";
    private string _peerName = "";
    private string _pairCode = "";
    private string _hostAddress = "";
    private int _port = DefaultPort;
    private string? _error;
    private int _generation;

    public event Action<ChatStateDto>? StateChanged;
    public event Action<string, ChatMessageDto>? IncomingMessage;

    public ChatStateDto Snapshot()
    {
        lock (_gate)
        {
            return CreateStateLocked();
        }
    }

    public void Host(string localName, int port, bool loopbackOnly, string ownerNodeId = "")
    {
        ThrowIfDisposed();
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Choose a port between 1 and 65535.");
        }

        ResetSession(clearMessages: true);
        string name = NormalizeName(localName);
        string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var listener = new TcpListener(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port);
        listener.Start(4);
        int actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();
        int generation;
        lock (_gate)
        {
            generation = ++_generation;
            _cts = cts;
            _listener = listener;
            _active = true;
            _hosting = true;
            _mode = "host";
            _ownerNodeId = ownerNodeId ?? "";
            _localName = name;
            _peerName = "";
            _pairCode = code;
            _hostAddress = loopbackOnly ? IPAddress.Loopback.ToString() : NetworkPcm.GetLocalAddress();
            _port = actualPort;
            _error = null;
        }

        Notify();
        Task task = Task.Run(() => AcceptLoopAsync(
            listener, name, code, generation, cts.Token));
        _ = task.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
    }

    public void Join(string localName, string host, int port, string code, string ownerNodeId = "")
    {
        ThrowIfDisposed();
        string address = (host ?? "").Trim();
        if (address.Length == 0)
        {
            throw new ArgumentException("Enter the host address.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Choose a port between 1 and 65535.");
        }

        string pairing = (code ?? "").Trim();
        if (pairing.Length == 0)
        {
            throw new ArgumentException("Enter the pairing code.", nameof(code));
        }

        ResetSession(clearMessages: true);
        string name = NormalizeName(localName);
        var cts = new CancellationTokenSource();
        int generation;
        lock (_gate)
        {
            generation = ++_generation;
            _cts = cts;
            _active = true;
            _hosting = false;
            _mode = "join";
            _ownerNodeId = ownerNodeId ?? "";
            _localName = name;
            _peerName = "";
            _pairCode = "";
            _hostAddress = address;
            _port = port;
            _error = null;
        }

        Notify();
        Task task = Task.Run(() => JoinAsync(
            address, port, pairing, name, generation, cts.Token));
        _ = task.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
    }

    public void Stop()
    {
        StopInternal();
        Notify();
    }

    /// <summary>Returns null when the message was accepted, or a user-facing reason.</summary>
    public string? TrySend(string text)
    {
        if (_disposed)
        {
            return "Chat is not available.";
        }

        string messageText = NormalizeText(text);
        if (messageText.Length == 0)
        {
            return "Type a message first.";
        }

        if (messageText.Length > MaxTextLength)
        {
            return $"Messages can be up to {MaxTextLength} characters.";
        }

        NetworkStream stream;
        CancellationToken token;
        ChatMessageDto message;
        int generation;
        lock (_gate)
        {
            if (!_connected || _stream == null || _cts == null)
            {
                return "Not connected.";
            }

            stream = _stream;
            token = _cts.Token;
            generation = _generation;
            message = new ChatMessageDto
            {
                Id = Guid.NewGuid().ToString("N"),
                Sender = _localName,
                Text = messageText,
                SentAt = DateTimeOffset.UtcNow.ToString("O"),
                Mine = true,
            };
            AddMessageLocked(message);
        }

        Notify();
        var frame = new ChatFrame
        {
            Type = "message",
            Id = message.Id,
            Text = message.Text,
            SentAt = message.SentAt,
        };
        _ = SendFrameAsync(stream, frame, generation, token);
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopInternal();
        StateChanged = null;
        IncomingMessage = null;
    }

    private async Task AcceptLoopAsync(
        TcpListener listener, string localName, string code, int generation, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                break;
            }

            NetworkStream stream;
            try
            {
                stream = client.GetStream();
            }
            catch (Exception)
            {
                client.Dispose();
                continue;
            }

            bool accepted;
            lock (_gate)
            {
                accepted = generation == _generation && !_connected && _client == null;
                if (accepted)
                {
                    _client = client;
                    _stream = stream;
                }
            }

            if (!accepted)
            {
                stream.Dispose();
                client.Dispose();
                continue;
            }

            await HostSessionAsync(
                client, stream, localName, code, generation, token);
        }
    }

    private async Task HostSessionAsync(
        TcpClient client, NetworkStream stream, string localName, string code,
        int generation, CancellationToken token)
    {
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(HandshakeTimeout);
            byte[] nonceBytes = RandomNumberGenerator.GetBytes(18);
            string nonce = Convert.ToBase64String(nonceBytes);
            await WriteFrameAsync(stream, new ChatFrame
            {
                Type = "challenge",
                Protocol = ProtocolVersion,
                Nonce = nonce,
            }, handshake.Token);

            ChatFrame hello = await ReadFrameAsync(stream, handshake.Token);
            if (hello.Type != "hello" || hello.Protocol != ProtocolVersion)
            {
                await RejectHostAsync(stream, "The peer sent an invalid handshake.", generation, client, handshake.Token);
                return;
            }

            string expected = ComputeProof(nonce, code);
            if (!FixedEquals(expected, hello.Proof))
            {
                await RejectHostAsync(stream, "The pairing code was rejected.", generation, client, handshake.Token);
                return;
            }

            string peerName = NormalizeName(hello.Name, "Peer");
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }

                _peerName = peerName;
                _connected = true;
                _error = null;
            }

            await WriteFrameAsync(stream, new ChatFrame
            {
                Type = "welcome",
                Protocol = ProtocolVersion,
                Name = localName,
                Proof = ComputeProof(nonce, "host:" + code),
            }, handshake.Token);

            Notify();
            await RunSessionAsync(client, stream, generation, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CloseConnection(generation, client, stream, null);
        }
        catch (Exception)
        {
            CloseConnection(generation, client, stream, "The chat handshake failed.");
        }
    }

    private async Task RejectHostAsync(
        NetworkStream stream, string reason, int generation, TcpClient client, CancellationToken token)
    {
        try
        {
            await WriteFrameAsync(stream, new ChatFrame
            {
                Type = "error",
                Message = reason,
            }, token);
        }
        catch (Exception)
        {
        }

        CloseConnection(generation, client, stream, reason);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task JoinAsync(
        string address, int port, string code, string localName, int generation, CancellationToken token)
    {
        TcpClient? client = null;
        NetworkStream? stream = null;
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
            connect.CancelAfter(ConnectTimeout);
            client = new TcpClient();
            await client.ConnectAsync(address, port, connect.Token);
            stream = client.GetStream();

            lock (_gate)
            {
                if (generation != _generation)
                {
                    stream.Dispose();
                    client.Dispose();
                    return;
                }

                _client = client;
                _stream = stream;
            }

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(HandshakeTimeout);
            ChatFrame challenge = await ReadFrameAsync(stream, handshake.Token);
            if (challenge.Type == "error")
            {
                throw new ChatException(RemoteError(challenge.Message, "The host rejected the connection."));
            }

            if (challenge.Type != "challenge" || challenge.Protocol != ProtocolVersion)
            {
                throw new ChatException("The host sent an invalid handshake.");
            }

            await WriteFrameAsync(stream, new ChatFrame
            {
                Type = "hello",
                Protocol = ProtocolVersion,
                Name = localName,
                Proof = ComputeProof(challenge.Nonce, code),
            }, handshake.Token);

            ChatFrame welcome = await ReadFrameAsync(stream, handshake.Token);
            if (welcome.Type == "error")
            {
                throw new ChatException(RemoteError(welcome.Message, "The pairing code was rejected."));
            }

            if (welcome.Type != "welcome" || welcome.Protocol != ProtocolVersion ||
                !FixedEquals(ComputeProof(challenge.Nonce, "host:" + code), welcome.Proof))
            {
                throw new ChatException("The host could not be verified.");
            }

            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }

                _peerName = NormalizeName(welcome.Name, "Host");
                _connected = true;
                _error = null;
            }

            Notify();
            await RunSessionAsync(client, stream, generation, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CloseConnection(generation, client, stream, null);
        }
        catch (OperationCanceledException)
        {
            CloseConnection(generation, client, stream,
                "Could not reach the host. Check the address and port.");
        }
        catch (ChatException ex)
        {
            CloseConnection(generation, client, stream, ex.Message);
        }
        catch (SocketException)
        {
            CloseConnection(generation, client, stream,
                "Could not reach the host. Check the address and port.");
        }
        catch (EndOfStreamException)
        {
            CloseConnection(generation, client, stream, "The host closed the connection.");
        }
        catch (Exception)
        {
            CloseConnection(generation, client, stream, "The chat connection failed.");
        }
    }

    private async Task RunSessionAsync(
        TcpClient client, NetworkStream stream, int generation, CancellationToken token)
    {
        string? error = null;
        const double refillPerSecond = 20;
        const double burst = 40;
        double tokens = burst;
        long lastRefill = Stopwatch.GetTimestamp();
        try
        {
            while (!token.IsCancellationRequested)
            {
                ChatFrame frame = await ReadFrameAsync(stream, token);
                switch (frame.Type)
                {
                    case "message":
                        long now = Stopwatch.GetTimestamp();
                        tokens = Math.Min(burst,
                            tokens + (now - lastRefill) * refillPerSecond / Stopwatch.Frequency);
                        lastRefill = now;
                        if (tokens < 1)
                        {
                            await Task.Delay(100, token);
                            tokens = 0;
                        }
                        else
                        {
                            tokens -= 1;
                        }

                        AddIncoming(frame);
                        break;
                    case "ping":
                        await WriteFrameAsync(stream, new ChatFrame { Type = "pong" }, token);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (Exception)
        {
            error = "The chat connection was lost.";
        }
        finally
        {
            CloseConnection(generation, client, stream, error);
        }
    }

    private void AddIncoming(ChatFrame frame)
    {
        string text = NormalizeText(frame.Text);
        if (text.Length == 0 || text.Length > MaxTextLength)
        {
            return;
        }

        ChatMessageDto message;
        string ownerNodeId;
        lock (_gate)
        {
            ownerNodeId = _ownerNodeId;
            message = new ChatMessageDto
            {
                Id = NormalizeId(frame.Id),
                Sender = string.IsNullOrWhiteSpace(_peerName) ? "Peer" : _peerName,
                Text = text,
                SentAt = NormalizeTimestamp(frame.SentAt),
                Mine = false,
            };
            AddMessageLocked(message);
        }

        Notify();
        try
        {
            IncomingMessage?.Invoke(ownerNodeId, message);
        }
        catch (Exception)
        {
        }
    }

    private async Task SendFrameAsync(
        NetworkStream stream, ChatFrame frame, int generation, CancellationToken token)
    {
        try
        {
            await WriteFrameAsync(stream, frame, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (frame.Type == "message") RemoveMessageById(frame.Id);
        }
        catch (Exception)
        {
            if (frame.Type == "message") RemoveMessageById(frame.Id);
            CloseConnection(generation, stream, "The chat connection was lost.");
        }
    }

    private void RemoveMessageById(string id)
    {
        bool changed = false;
        lock (_gate)
        {
            int index = _messages.FindIndex(message => message.Mine && message.Id == id);
            if (index >= 0)
            {
                _messages.RemoveAt(index);
                changed = true;
            }
        }

        if (changed)
        {
            Notify();
        }
    }

    private void CloseConnection(
        int generation, TcpClient? client, NetworkStream? stream, string? error)
    {
        bool changed = false;
        lock (_gate)
        {
            if (generation == _generation && ReferenceEquals(_client, client))
            {
                if (_client != null || _stream != null || _connected)
                {
                    changed = true;
                }

                _client = null;
                _stream = null;
                _connected = false;
                _peerName = "";
                if (_mode == "join")
                {
                    _active = false;
                    _hosting = false;
                    error ??= "The chat connection closed.";
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    _error = error;
                }
            }
        }

        stream?.Dispose();
        client?.Dispose();
        if (changed)
        {
            Notify();
        }
    }

    private void CloseConnection(int generation, NetworkStream stream, string error)
    {
        TcpClient? client;
        lock (_gate)
        {
            client = ReferenceEquals(_stream, stream) ? _client : null;
        }

        CloseConnection(generation, client, stream, error);
    }

    private void StopInternal()
    {
        TcpListener? listener;
        TcpClient? client;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            ++_generation;
            listener = _listener;
            client = _client;
            cts = _cts;
            _listener = null;
            _client = null;
            _stream = null;
            _cts = null;
            _active = false;
            _hosting = false;
            _connected = false;
            _mode = "";
            _ownerNodeId = "";
            _peerName = "";
            _pairCode = "";
            _error = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        client?.Dispose();
    }

    private void ResetSession(bool clearMessages)
    {
        TcpListener? listener;
        TcpClient? client;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            ++_generation;
            listener = _listener;
            client = _client;
            cts = _cts;
            _listener = null;
            _client = null;
            _stream = null;
            _cts = null;
            _active = false;
            _hosting = false;
            _connected = false;
            _mode = "";
            _ownerNodeId = "";
            _peerName = "";
            _pairCode = "";
            _error = null;
            if (clearMessages)
            {
                _messages.Clear();
            }
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        client?.Dispose();
    }

    private void AddMessageLocked(ChatMessageDto message)
    {
        _messages.Add(message);
        if (_messages.Count > MaxHistory)
        {
            _messages.RemoveRange(0, _messages.Count - MaxHistory);
        }
    }

    private ChatStateDto CreateStateLocked() => new()
    {
        Active = _active,
        Hosting = _hosting,
        Connected = _connected,
        Mode = _mode,
        OwnerNodeId = _ownerNodeId,
        LocalName = _localName,
        PeerName = _peerName,
        PairCode = _pairCode,
        HostAddress = _hostAddress,
        Port = _port,
        Error = _error,
        Messages = _messages.ToList(),
    };

    private void Notify()
    {
        lock (_notifyGate)
        {
            ChatStateDto state;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                state = CreateStateLocked();
            }

            try
            {
                StateChanged?.Invoke(state);
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task<ChatFrame> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException("Invalid chat frame length.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<ChatFrame>(payload, Json)
               ?? throw new InvalidDataException("Empty chat frame.");
    }

    private async Task WriteFrameAsync(
        NetworkStream stream, ChatFrame frame, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidDataException("Chat frame is too large.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await _writeGate.WaitAsync(token);
        try
        {
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(payload, token);
            await stream.FlushAsync(token);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string RemoteError(string? message, string fallback)
    {
        string text = new((message ?? "")
            .Where(character => !char.IsControl(character))
            .ToArray());
        text = text.Trim();
        if (text.Length == 0)
        {
            text = fallback;
        }

        return text.Length <= 200 ? text : text[..200];
    }

    private static string ComputeProof(string nonce, string code)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(nonce + ":" + code));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedEquals(string expected, string actual)
    {
        if (string.IsNullOrEmpty(actual))
        {
            return false;
        }

        byte[] left = Encoding.UTF8.GetBytes(expected);
        byte[] right = Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string NormalizeName(string? name, string fallback = "Ruse user")
    {
        string cleaned = new((name ?? "")
            .Where(character => !char.IsControl(character))
            .ToArray());
        cleaned = cleaned.Trim();
        if (cleaned.Length == 0)
        {
            cleaned = fallback;
        }

        return cleaned.Length <= MaxNameLength ? cleaned : cleaned[..MaxNameLength];
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        string normalized = text.Replace("\0", "").Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.Trim();
    }

    private static string NormalizeId(string? id)
    {
        string value = (id ?? "").Trim();
        return value.Length is > 0 and <= 64 ? value : Guid.NewGuid().ToString("N");
    }

    private static string NormalizeTimestamp(string? value)
    {
        return value is { Length: > 0 and <= 64 } &&
               DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed.ToUniversalTime().ToString("O")
            : DateTimeOffset.UtcNow.ToString("O");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ChatFrame
    {
        public string Type { get; set; } = "";
        public int Protocol { get; set; }
        public string Name { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Proof { get; set; } = "";
        public string Message { get; set; } = "";
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string SentAt { get; set; } = "";
    }

    private sealed class ChatException : Exception
    {
        public ChatException(string message, string fallback)
            : base(string.IsNullOrWhiteSpace(message) ? fallback : message)
        {
        }

        public ChatException(string message)
            : base(message)
        {
        }
    }
}
