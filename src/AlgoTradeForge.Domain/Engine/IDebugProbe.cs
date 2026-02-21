namespace AlgoTradeForge.Domain.Engine;

/// <summary>
/// Callback contract that lets an external controller observe and gate engine execution.
/// The engine calls methods at specific event points. The probe can block to implement stepping.
/// </summary>
public interface IDebugProbe
{
    /// <summary>
    /// True if probe is active. When false, the engine skips all probe call sites.
    /// Evaluated once at the start of <see cref="BacktestEngine.Run"/> and cached locally.
    /// </summary>
    bool IsActive { get; }

    /// <summary>Called after strategy.OnInit() completes, before the main bar loop starts.</summary>
    void OnRunStart();

    /// <summary>Called after a bar is fully processed (OnBarStart → fills → SL/TP → OnBarComplete).</summary>
    void OnBarProcessed(DebugSnapshot snapshot);

    /// <summary>
    /// Called after an event is emitted by the EventBus. Allows event-level stepping.
    /// Default no-op — only active probes override this.
    /// </summary>
    void OnEventEmitted(string eventType, long sequenceNumber) { }

    /// <summary>Called when the main loop exits (all bars exhausted or cancellation).</summary>
    void OnRunEnd();
}
