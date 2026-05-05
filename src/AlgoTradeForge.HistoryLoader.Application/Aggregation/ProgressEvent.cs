namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Progress events emitted by the aggregation pipeline and forwarded to the SSE consumer.
/// One sealed subtype per state — pattern-match on subtype at the consumer.
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
    /// Terminal state distinct from <see cref="Error"/>. Emitted when the user cancels via
    /// <c>DELETE /api/v1/aggregations/{jobId}</c>; staging dir is deleted and no manifest entry
    /// is written. <see cref="Reason"/> identifies the cancel source (e.g. <c>"user_cancelled"</c>).
    /// </summary>
    public sealed record Cancelled(
        string JobId,
        string Reason,
        DateTimeOffset AtUtc) : ProgressEvent;
}
