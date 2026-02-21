using System.Net.WebSockets;
using System.Text.Json;
using AlgoTradeForge.Application.Debug;

namespace AlgoTradeForge.WebApi.Endpoints;

/// <summary>
/// Handles WebSocket connections for debug sessions.
/// Replaces the HTTP POST /commands endpoint for real-time event streaming and command control.
/// Protocol:
///   Server → Client: raw JSONL events (same format as file sink), snapshot responses, error/completion messages
///   Client → Server: command JSON messages (same format as DebugCommandRequest)
/// </summary>
public static class DebugWebSocketHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void MapDebugWebSocket(this IEndpointRouteBuilder app)
    {
        app.Map("/api/debug-sessions/{id:guid}/ws", HandleWebSocket);
    }

    private static async Task HandleWebSocket(HttpContext context, Guid id, IDebugSessionStore store)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection expected.");
            return;
        }

        var session = store.Get(id);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync($"Debug session '{id}' not found.");
            return;
        }

        if (session.WebSocketSink is null)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Session has no WebSocket sink configured.");
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var ct = context.RequestAborted;

        // Attach the WebSocket to the sink so events start streaming
        session.WebSocketSink.Attach(webSocket, ct);

        try
        {
            await RunCommandLoop(webSocket, session, ct);
        }
        catch (WebSocketException) { /* Client disconnected */ }
        catch (OperationCanceledException) { /* Request aborted */ }
    }

    private const int MaxMessageSize = 64 * 1024; // 64 KB

    private static async Task RunCommandLoop(WebSocket webSocket, DebugSession session, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var accumulator = new MemoryStream();

        while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await webSocket.ReceiveAsync(buffer.AsMemory(), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Client requested close", ct);
                break;
            }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            accumulator.Write(buffer, 0, result.Count);

            if (accumulator.Length > MaxMessageSize)
            {
                SendError(session.WebSocketSink!, "Message exceeds maximum size of 64 KB.");
                accumulator.SetLength(0);
                // Drain remaining frames of this message
                while (!result.EndOfMessage)
                    result = await webSocket.ReceiveAsync(buffer.AsMemory(), ct);
                continue;
            }

            if (!result.EndOfMessage)
                continue;

            var messageBytes = accumulator.ToArray().AsMemory();
            accumulator.SetLength(0);

            await ProcessCommand(session, messageBytes, ct);
        }
    }

    private static async Task ProcessCommand(
        DebugSession session, ReadOnlyMemory<byte> messageBytes, CancellationToken ct)
    {
        var sink = session.WebSocketSink!;
        DebugCommand? command;
        string? error;

        try
        {
            (command, error) = DebugCommandParser.Parse(messageBytes);
        }
        catch (JsonException ex)
        {
            SendError(sink, $"Invalid JSON: {ex.Message}");
            return;
        }

        if (command is null)
        {
            SendError(sink, error ?? "Unknown error");
            return;
        }

        try
        {
            var snapshot = await session.Probe.SendCommandAsync(command, ct);
            SendSnapshot(sink, snapshot, session.Probe.IsRunning);
        }
        catch (InvalidOperationException ex)
        {
            SendError(sink, ex.Message);
        }
    }

    private static void SendSnapshot(
        Application.Events.WebSocketSink sink, Domain.Engine.DebugSnapshot snapshot, bool sessionActive)
    {
        var response = new WebSocketSnapshotMessage(
            "snapshot", sessionActive, snapshot.SequenceNumber, snapshot.TimestampMs,
            snapshot.SubscriptionIndex, snapshot.IsExportableSubscription,
            snapshot.FillsThisBar, snapshot.PortfolioEquity);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        sink.SendMessage(bytes);
    }

    private static void SendError(Application.Events.WebSocketSink sink, string message)
    {
        var response = new WebSocketErrorMessage("error", message);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        sink.SendMessage(bytes);
    }

    private sealed record WebSocketSnapshotMessage(
        string Type,
        bool SessionActive,
        long SequenceNumber,
        long TimestampMs,
        int SubscriptionIndex,
        bool IsExportableSubscription,
        int FillsThisBar,
        long PortfolioEquity);

    private sealed record WebSocketErrorMessage(
        string Type,
        string Message);
}
