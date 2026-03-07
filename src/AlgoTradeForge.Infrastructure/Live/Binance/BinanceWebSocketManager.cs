using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.Live.Binance;

public sealed class BinanceWebSocketManager : IAsyncDisposable
{
    private readonly string _wsUrl;
    private readonly TimeSpan _reconnectDelay;
    private readonly int _maxReconnectAttempts;
    private readonly ILogger _logger;
    private readonly Lock _connectionsLock = new();
    private readonly List<(ClientWebSocket Socket, Task ReadTask)> _connections = [];
    private CancellationTokenSource? _cts;

    public BinanceWebSocketManager(
        string wsUrl,
        TimeSpan reconnectDelay, int maxReconnectAttempts,
        ILogger logger)
    {
        _wsUrl = wsUrl;
        _reconnectDelay = reconnectDelay;
        _maxReconnectAttempts = maxReconnectAttempts;
        _logger = logger;
    }

    public void Start(CancellationTokenSource cts) => _cts = cts;

    public Task SubscribeKline(string symbol, string interval, Action<BinanceKlineMessage> onMessage)
    {
        var stream = $"{symbol.ToLowerInvariant()}@kline_{interval}";
        var url = $"{_wsUrl}/ws/{stream}";
        return ConnectStream(url, stream, buffer =>
        {
            var msg = JsonSerializer.Deserialize<BinanceKlineMessage>(buffer.Span, BinanceJsonOptions.Default);
            if (msg is not null)
                onMessage(msg);
        });
    }

    /// <summary>
    /// Connect to the Binance WebSocket API, send <c>userDataStream.subscribe.signature</c>,
    /// and return once subscribed. The read loop and reconnect logic continue in background.
    /// </summary>
    public async Task ConnectUserDataWsApi(
        string wsApiUrl, string apiKey, Func<string, string> signFunc,
        Func<long> getTimestamp,
        Action<BinanceExecutionReport> onExecution)
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        Action<ReadOnlyMemory<byte>> onMessage = buffer =>
        {
            using var doc = JsonDocument.Parse(buffer);
            var root = doc.RootElement;

            // WS API responses have "status" — log errors, skip confirmations
            if (root.TryGetProperty("status", out var statusProp))
            {
                var status = statusProp.GetInt32();
                if (status != 200)
                    _logger.LogError("userDataStream.subscribe failed (status={Status}): {Response}",
                        status, root.GetRawText());
                else
                    _logger.LogDebug("userDataStream.subscribe confirmed (status=200)");
                return;
            }

            // User data events are wrapped: {"subscriptionId": N, "event": {...}}
            if (!root.TryGetProperty("event", out var eventElement))
                return;

            if (!eventElement.TryGetProperty("e", out var eventTypeProp))
                return;

            if (eventTypeProp.GetString() == "executionReport")
            {
                var rawText = eventElement.GetRawText();
                var report = JsonSerializer.Deserialize<BinanceExecutionReport>(rawText, BinanceJsonOptions.Default);
                if (report is not null)
                    onExecution(report);
            }
        };

        // Initial connect — awaited so caller knows subscription is active
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsApiUrl), ct);
        _logger.LogInformation("Connected to userData-wsapi");
        await SendSubscribeSignature(ws, apiKey, signFunc, getTimestamp);

        var readTask = ReadLoop(ws, "userData-wsapi", onMessage, ct);
        lock (_connectionsLock)
        {
            _connections.Add((ws, readTask));
        }

        // Background reconnect loop
        _ = Task.Run(async () =>
        {
            var attempts = 0;
            try { await readTask; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "userData-wsapi disconnected, will reconnect");
            }

            while (!ct.IsCancellationRequested && attempts < _maxReconnectAttempts)
            {
                attempts++;
                var delay = TimeSpan.FromSeconds(_reconnectDelay.TotalSeconds * Math.Pow(2, attempts - 1));
                await Task.Delay(delay, ct);

                var reconnectWs = new ClientWebSocket();
                try
                {
                    _logger.LogInformation("Reconnecting userData-wsapi (attempt {Attempt}/{Max})",
                        attempts, _maxReconnectAttempts);
                    await reconnectWs.ConnectAsync(new Uri(wsApiUrl), ct);
                    await SendSubscribeSignature(reconnectWs, apiKey, signFunc, getTimestamp);
                    _logger.LogInformation("Reconnected to userData-wsapi");
                    attempts = 0;

                    var reconnectReadTask = ReadLoop(reconnectWs, "userData-wsapi", onMessage, ct);
                    lock (_connectionsLock)
                    {
                        _connections.Add((reconnectWs, reconnectReadTask));
                    }
                    await reconnectReadTask;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    reconnectWs.Dispose();
                    _logger.LogWarning(ex, "userData-wsapi reconnect failed ({Attempt}/{Max})",
                        attempts, _maxReconnectAttempts);
                }
            }
        }, ct);
    }

    private async Task SendSubscribeSignature(ClientWebSocket ws, string apiKey, Func<string, string> signFunc, Func<long> getTimestamp)
    {
        var timestamp = getTimestamp();
        var queryString = $"apiKey={apiKey}&timestamp={timestamp}";
        var signature = signFunc(queryString);

        var message = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString(),
            method = "userDataStream.subscribe.signature",
            @params = new
            {
                apiKey,
                timestamp,
                signature,
            }
        });

        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
        _logger.LogInformation("Sent userDataStream.subscribe.signature request");
    }

    private async Task ConnectStream(string url, string streamName, Action<ReadOnlyMemory<byte>> onMessage)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        var attempts = 0;

        while (!ct.IsCancellationRequested && attempts < _maxReconnectAttempts)
        {
            var ws = new ClientWebSocket();
            try
            {
                _logger.LogInformation("Connecting to {Stream} (attempt {Attempt})", streamName, attempts + 1);
                await ws.ConnectAsync(new Uri(url), ct);
                _logger.LogInformation("Connected to {Stream}", streamName);
                attempts = 0;

                var readTask = ReadLoop(ws, streamName, onMessage, ct);
                lock (_connectionsLock)
                {
                    _connections.Add((ws, readTask));
                }
                await readTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                attempts++;
                _logger.LogWarning(ex, "WebSocket {Stream} disconnected, reconnecting ({Attempt}/{Max})",
                    streamName, attempts, _maxReconnectAttempts);

                ws.Dispose();
                if (attempts < _maxReconnectAttempts)
                {
                    var delay = TimeSpan.FromSeconds(_reconnectDelay.TotalSeconds * Math.Pow(2, attempts - 1));
                    await Task.Delay(delay, ct);
                }
            }
        }
    }

    private async Task ReadLoop(
        ClientWebSocket ws, string streamName,
        Action<ReadOnlyMemory<byte>> onMessage, CancellationToken ct)
    {
        var buffer = new byte[8192];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogInformation("WebSocket {Stream} received close frame", streamName);
                break;
            }

            try
            {
                onMessage(new ReadOnlyMemory<byte>(ms.GetBuffer(), 0, (int)ms.Length));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message from {Stream}", streamName);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<(ClientWebSocket Socket, Task ReadTask)> snapshot;
        lock (_connectionsLock)
        {
            snapshot = [.. _connections];
            _connections.Clear();
        }

        // Close sockets to trigger read loop exits
        foreach (var (socket, _) in snapshot)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch { /* best effort */ }
        }

        // Await all read tasks to ensure clean shutdown
        foreach (var (_, readTask) in snapshot)
        {
            try { await readTask; }
            catch { /* best effort */ }
        }

        // Dispose sockets after read loops have exited
        foreach (var (socket, _) in snapshot)
            socket.Dispose();
    }
}
