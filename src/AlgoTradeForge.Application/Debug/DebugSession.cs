using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Events;

namespace AlgoTradeForge.Application.Debug;

/// <summary>
/// Represents a single debug session. Owns the probe and tracks metadata.
/// </summary>
public sealed class DebugSession : IAsyncDisposable, IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public GatingDebugProbe Probe { get; } = new();
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public required string AssetName { get; init; }
    public required string StrategyName { get; init; }

    /// <summary>
    /// The task representing the background engine run. Set after launch.
    /// </summary>
    public Task<BacktestResultDto>? RunTask { get; internal set; }

    /// <summary>
    /// The event bus for this run. Phase 3 wires this into the engine.
    /// </summary>
    public IEventBus? EventBus { get; internal set; }

    /// <summary>
    /// The event sink for this run. Disposed when the session ends.
    /// </summary>
    public IDisposable? EventSink { get; internal set; }

    /// <summary>
    /// The WebSocket sink for real-time event streaming.
    /// Created at session start, WebSocket attached when client connects.
    /// </summary>
    public WebSocketSink? WebSocketSink { get; internal set; }

    /// <summary>
    /// Cancellation source for the engine run.
    /// </summary>
    public CancellationTokenSource Cts { get; } = new();

    public async ValueTask DisposeAsync()
    {
        Cts.Cancel();
        Probe.Dispose();
        if (RunTask is not null)
            try { await RunTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* engine threw — already logged elsewhere */ }
        if (WebSocketSink is not null)
            await WebSocketSink.DisposeAsync().ConfigureAwait(false);
        EventSink?.Dispose();
        Cts.Dispose();
    }

    public void Dispose()
    {
        Cts.Cancel();
        Probe.Dispose();
        // Don't dispose EventSink or CTS synchronously — engine thread may still
        // be running (e.g. WriteMeta). Prefer DisposeAsync for proper cleanup.
    }
}
