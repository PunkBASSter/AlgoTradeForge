namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Progress events emitted by <see cref="AggregationPipeline"/> and forwarded to the SSE
/// consumer (TRD §5.4). One sealed type per state — pattern-match on subtype at the consumer.
/// </summary>
public abstract record ProgressEvent
{
    public sealed record Queued(
        string JobId,
        string FeedId,
        DateTimeOffset QueuedAt,
        int QueuePosition) : ProgressEvent;

    public sealed record Started(
        string JobId,
        string FeedId,
        DateTimeOffset StartedAt,
        string SourceFeedId) : ProgressEvent;

    public sealed record Progress(
        string JobId,
        string? CurrentPartition,
        long BarsEmitted,
        long ElapsedMs) : ProgressEvent;

    public sealed record Complete(AggregationResult Result) : ProgressEvent;

    public sealed record Error(
        string JobId,
        string Code,
        string Message,
        bool Retryable) : ProgressEvent;

    /// <summary>
    /// Phase 6 — terminal state distinct from <see cref="Error"/>. Emitted when the user
    /// explicitly cancels via <c>DELETE /api/v1/aggregations/{jobId}</c>; the staging dir is
    /// recursively deleted and no manifest entry is written. <see cref="Reason"/> distinguishes
    /// <c>"user_cancelled"</c> (per-job CTS fired) from any future programmatic cancel paths.
    /// </summary>
    public sealed record Cancelled(
        string JobId,
        string Reason,
        DateTimeOffset AtUtc) : ProgressEvent;
}
