using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ruse.App.Diagnostics;

namespace Ruse.App;

/// <summary>
/// Loopback-only control API for local apps. HTTP handles discovery and one-shot
/// changes; the WebSocket carries high-rate parameter control and graph events.
/// </summary>
internal sealed class ControlServer : IDisposable
{
    private const int ApiVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions DiscoveryJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly WebShellController _owner;
    private readonly ConcurrentDictionary<Guid, ControlSocket> _clients = new();
    private WebApplication? _app;
    private string _token = "";
    private string? _lastGraphJson;
    private long _graphVersion;
    private int _port;
    private bool _disposed;

    public ControlServer(WebShellController owner)
    {
        _owner = owner;
    }

    internal bool HasClients => !_clients.IsEmpty;
    internal string Url => $"http://127.0.0.1:{_port}";
    internal string Token => _token;

    public static string DiscoveryPath => Path.Combine(AppSettings.Directory, "control.json");

    public static void DeleteDiscoveryFile()
    {
        try
        {
            File.Delete(DiscoveryPath);
        }
        catch
        {
        }
    }

    public async Task StartAsync(int preferredPort)
    {
        DeleteDiscoveryFile();
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _port = FindFreePort(preferredPort);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, _port);
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!IsAllowedHost(context) || !IsAllowedOrigin(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!IsAuthorized(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/api/v1/state", async () =>
            Results.Json(await _owner.Dispatcher.InvokeAsync(_owner.BuildControlState), Json));
        app.MapGet("/api/v1/nodes", async () =>
            Results.Json(await _owner.Dispatcher.InvokeAsync(_owner.BuildControlNodes), Json));
        app.MapGet("/api/v1/telemetry", async () =>
            Results.Json(await _owner.Dispatcher.InvokeAsync(_owner.BuildControlTelemetry), Json));
        app.MapPatch("/api/v1/nodes/{id}", async (string id, ControlNodePatchRequest? request) =>
        {
            if (request == null)
            {
                return Results.BadRequest(new { error = "A JSON body is required." });
            }

            return await _owner.Dispatcher.InvokeAsync<IResult>(() =>
                _owner.TryPatchControlNode(id, request, out string error)
                    ? Results.Ok(new { ok = true })
                    : Results.BadRequest(new { error }));
        });
        app.MapPatch("/api/v1/nodes/{id}/parameters/{processor:int}/{parameter:int}",
            async (string id, int processor, int parameter, ControlSetParameterRequest? request) =>
            {
                if (request == null)
                {
                    return Results.BadRequest(new { error = "A JSON body is required." });
                }

                return await _owner.Dispatcher.InvokeAsync<IResult>(() =>
                    _owner.TrySetControlParameter(
                        id, processor, parameter, request.Value, request.Normalized, out string error)
                        ? Results.Ok(new { ok = true })
                        : Results.BadRequest(new { error }));
            });
        app.MapPost("/api/v1/transport", async (ControlTransportRequest? request) =>
        {
            if (request == null)
            {
                return Results.BadRequest(new { error = "A JSON body is required." });
            }

            return await _owner.Dispatcher.InvokeAsync<IResult>(() =>
                _owner.TrySetControlTransport(request.Running, out string error)
                    ? Results.Ok(new { ok = true, running = request.Running })
                    : Results.BadRequest(new { error }));
        });
        app.Map("/api/v1/events", HandleWebSocketAsync);

        _app = app;
        await app.StartAsync().ConfigureAwait(false);
        WriteDiscoveryFile();
        AppLog.Info($"Control API listening at http://127.0.0.1:{_port}; discovery file: {DiscoveryPath}");
    }

    public void PublishGraph(int version, bool running, List<ControlNodeDto> nodes)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        _graphVersion = version;
        string json = JsonSerializer.Serialize(new
        {
            type = "graph",
            version,
            running,
            nodes,
        }, Json);
        _lastGraphJson = json;
        Broadcast(json, reliable: true);
    }

    public void PublishNodeState(NodeStateDto state)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        string json = JsonSerializer.Serialize(new
        {
            type = "node-state",
            graphVersion = _graphVersion,
            payload = state,
        }, Json);
        Broadcast(json, reliable: false);
    }

    public void PublishLinkState(LinkStateDto state)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        string json = JsonSerializer.Serialize(new
        {
            type = "link-state",
            graphVersion = _graphVersion,
            payload = state,
        }, Json);
        Broadcast(json, reliable: false);
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var client = new ControlSocket(socket);
        var id = Guid.NewGuid();
        _clients[id] = client;
        try
        {
            await client.SendAsync(JsonSerializer.Serialize(new
            {
                type = "hello",
                apiVersion = ApiVersion,
            }, Json));

            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open && !_disposed)
            {
                string? text = await ReceiveTextAsync(socket, buffer, context.RequestAborted);
                if (text == null)
                {
                    break;
                }

                if (IsResyncRequest(text))
                {
                    string graph = _lastGraphJson ??
                        JsonSerializer.Serialize(new
                        {
                            type = "error",
                            message = "The graph is not available yet.",
                        }, Json);
                    if (!client.Enqueue(graph, reliable: true))
                    {
                        break;
                    }

                    continue;
                }

                string reply = await _owner.Dispatcher.InvokeAsync(
                    () => _owner.HandleControlWebSocketMessage(text));
                await client.SendAsync(reply);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
        }
    }

    private static async Task<string?> ReceiveTextAsync(
        WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                if (result.EndOfMessage)
                {
                    return null;
                }

                continue;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private void Broadcast(string json, bool reliable)
    {
        foreach (var pair in _clients)
        {
            if (!pair.Value.Enqueue(json, reliable))
            {
                if (_clients.TryRemove(pair.Key, out var removed))
                {
                    removed.Dispose();
                }
            }
        }
    }

    private static bool IsResyncRequest(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("type", out var type) &&
                   type.ValueKind == JsonValueKind.String &&
                   string.Equals(type.GetString(), "resync", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool IsAuthorized(HttpContext context)
    {
        string? token = null;
        string header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = header[7..].Trim();
        }

        if (string.IsNullOrEmpty(token) &&
            string.Equals(context.Request.Path.Value, "/api/v1/events", StringComparison.OrdinalIgnoreCase))
        {
            token = context.Request.Query["token"].ToString();
        }

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        byte[] provided = Encoding.UTF8.GetBytes(token);
        byte[] expected = Encoding.UTF8.GetBytes(_token);
        return provided.Length == expected.Length &&
               CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static bool IsAllowedHost(HttpContext context)
    {
        string host = context.Request.Host.Host;
        return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedOrigin(HttpContext context)
    {
        string origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin) ||
            string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static int FindFreePort(int preferred)
    {
        if (preferred > 0 && IsPortFree(preferred))
        {
            return preferred;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void WriteDiscoveryFile()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            string text = JsonSerializer.Serialize(new
            {
                url = $"http://127.0.0.1:{_port}",
                token = _token,
                pid = Environment.ProcessId,
                startedAt = DateTimeOffset.UtcNow,
                apiVersion = ApiVersion,
            }, DiscoveryJson);
            File.WriteAllText(DiscoveryPath, text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not write the control discovery file: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();

        var app = _app;
        _app = null;
        if (app != null)
        {
            try
            {
                app.StopAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            try
            {
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        DeleteDiscoveryFile();
    }

    private sealed class ControlSocket : IDisposable
    {
        private const int OutboxCapacity = 64;
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _outboxSignal = new(0);
        private readonly object _outboxLock = new();
        private readonly LinkedList<Outbound> _outbox = new();
        private readonly Task _pump;
        private bool _disposed;

        public ControlSocket(WebSocket socket)
        {
            _socket = socket;
            _pump = PumpAsync();
        }

        /// <summary>
        /// Queues an event. State deltas are droppable; structural graph and
        /// resync messages are reliable and evict older deltas first.
        /// </summary>
        public bool Enqueue(string json, bool reliable)
        {
            lock (_outboxLock)
            {
                if (_disposed)
                {
                    return false;
                }

                if (_outbox.Count >= OutboxCapacity)
                {
                    if (reliable)
                    {
                        if (!RemoveOldestStateLocked())
                        {
                            return false;
                        }
                    }
                    else if (!RemoveOldestStateLocked())
                    {
                        // All queued messages are reliable: drop this delta.
                        return true;
                    }
                }

                _outbox.AddLast(new Outbound(json, reliable));
            }

            _outboxSignal.Release();
            return true;
        }

        public async Task SendAsync(string json)
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await SendLockedAsync(json).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private bool RemoveOldestStateLocked()
        {
            for (var node = _outbox.First; node != null; node = node.Next)
            {
                if (!node.Value.Reliable)
                {
                    _outbox.Remove(node);
                    return true;
                }
            }

            return false;
        }

        private async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    await _outboxSignal.WaitAsync().ConfigureAwait(false);

                    Outbound? message = null;
                    lock (_outboxLock)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        var first = _outbox.First;
                        if (first != null)
                        {
                            _outbox.RemoveFirst();
                            message = first.Value;
                        }
                    }

                    if (message == null)
                    {
                        continue;
                    }

                    await _sendLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await SendLockedAsync(message.Json).ConfigureAwait(false);
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }
            }
            catch
            {
                // The socket or server was torn down; the client is removed
                // by the receive loop or by Dispose.
            }
        }

        private async Task SendLockedAsync(string json)
        {
            if (_disposed || _socket.State != WebSocketState.Open)
            {
                return;
            }

            await _socket.SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None).ConfigureAwait(false);
        }

        public void Dispose()
        {
            lock (_outboxLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _outbox.Clear();
            }

            _outboxSignal.Release();
            try
            {
                _socket.Dispose();
            }
            catch
            {
            }
        }

        private sealed record Outbound(string Json, bool Reliable);
    }
}
