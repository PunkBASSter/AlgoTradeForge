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

    public Task SubscribeUserData(string listenKey, Action<BinanceExecutionReport> onExecution)
    {
        var url = $"{_wsUrl}/ws/{listenKey}";
        return ConnectStream(url, "userData", buffer =>
        {
            using var doc = JsonDocument.Parse(buffer);
            var eventType = doc.RootElement.GetProperty("e").GetString();
            if (eventType == "executionReport")
            {
                var rawText = doc.RootElement.GetRawText();
                var report = JsonSerializer.Deserialize<BinanceExecutionReport>(rawText, BinanceJsonOptions.Default);
                if (report is not null)
                    onExecution(report);
            }
        });
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

        foreach (var (socket, _) in snapshot)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch { /* best effort */ }
            finally
            {
                socket.Dispose();
            }
        }
    }
}
