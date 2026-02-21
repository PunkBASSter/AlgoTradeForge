using System.Net.WebSockets;
using System.Threading.Channels;

namespace AlgoTradeForge.Application.Events;

/// <summary>
/// Sink that pushes serialized events to a connected WebSocket client in real-time.
/// If no client is connected, all writes are silently dropped (no-op).
/// Uses a bounded channel for backpressure — if the client is slow, oldest events are dropped.
/// </summary>
public sealed class WebSocketSink : ISink, IAsyncDisposable, IDisposable
{
    private readonly Channel<byte[]> _channel;
    private WebSocket? _webSocket;
    private Task? _sendLoop;
    private CancellationTokenSource? _sendCts;

    public WebSocketSink(int capacity = 1000)
    {
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Whether a WebSocket client is currently connected and open.</summary>
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    /// <summary>
    /// Attaches a WebSocket connection and starts the background send loop.
    /// Must be called exactly once after the WebSocket upgrade completes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if a WebSocket is already attached.</exception>
    public void Attach(WebSocket webSocket, CancellationToken ct)
    {
        if (_webSocket is not null)
            throw new InvalidOperationException("A WebSocket is already attached to this sink.");

        _webSocket = webSocket;
        _sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sendLoop = SendLoopAsync(_sendCts.Token);
    }

    /// <summary>
    /// Enqueues an event for delivery to the connected WebSocket client.
    /// If no client is connected or the channel is full, the event is silently dropped.
    /// </summary>
    public void Write(ReadOnlyMemory<byte> utf8Json)
    {
        if (_webSocket is null)
            return;

        // Copy bytes — the memory buffer may be recycled by the caller.
        // The send loop checks WebSocket state before sending.
        _channel.Writer.TryWrite(utf8Json.ToArray());
    }

    /// <summary>
    /// Enqueues a control message (snapshot, error, etc.) for delivery via the send loop.
    /// All writes go through the channel so the send loop is the sole WebSocket writer.
    /// </summary>
    public void SendMessage(ReadOnlyMemory<byte> utf8Json)
    {
        if (_webSocket is null)
            return;

        _channel.Writer.TryWrite(utf8Json.ToArray());
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var bytes in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_webSocket?.State == WebSocketState.Open)
                {
                    await _webSocket.SendAsync(
                        bytes.AsMemory(),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        if (_sendCts is not null)
        {
            await _sendCts.CancelAsync().ConfigureAwait(false);
            if (_sendLoop is not null)
            {
                try { await _sendLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _sendCts.Dispose();
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _sendCts?.Cancel();
        _sendCts?.Dispose();
    }
}
