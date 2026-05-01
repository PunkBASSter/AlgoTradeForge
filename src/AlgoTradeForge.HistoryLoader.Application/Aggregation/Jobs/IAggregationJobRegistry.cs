namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// In-memory registry of aggregation jobs (TRD §6.5). Owns the per-<c>feed_id</c> active-job
/// index that drives the 423 dedup check, and the per-job event log that drives SSE replay.
/// Phase 6 swaps this for a durable backing store.
/// </summary>
public interface IAggregationJobRegistry
{
    /// <summary>
    /// Atomically tries to enqueue a new job: checks active-feed_id index for 423,
    /// evicts terminal-with-same-feed_id within retention window (TRD §6.5 path b),
    /// pushes onto the queue, then records the job.
    /// </summary>
    EnqueueOutcome TryEnqueue(AggregationJob job, IAggregationJobQueue queue);

    /// <summary>Gets a job by id. Terminal records past retention return <c>null</c>.</summary>
    AggregationJobRecord? Get(string jobId);

    /// <summary>
    /// Returns the active (queued/running) job for a feed-id, or <c>null</c> when none is
    /// active. Used by the <c>POST /aggregate</c> endpoint to enforce the 423 → 409 precedence
    /// before calling <see cref="TryEnqueue"/>.
    /// </summary>
    ActiveJobInfo? CheckActiveFeedId(string feedId);

    /// <summary>
    /// Worker lifecycle: state transitions + event-log appends. The registry assigns event
    /// sequence numbers so SSE consumers can resume.
    /// </summary>
    void OnStarted(string jobId, string sourceFeedId);
    void OnProgress(string jobId, string? currentPartition, long barsEmitted, long elapsedMs);
    void OnCompleted(string jobId, AggregationResult result);
    void OnErrored(string jobId, string code, string message, bool retryable);
}

public sealed record ActiveJobInfo(string JobId, AggregationJobState State);

public abstract record EnqueueOutcome
{
    public sealed record Accepted(AggregationJobRecord Record) : EnqueueOutcome;

    public sealed record FeedAlreadyLocked(
        string ExistingJobId,
        AggregationJobState ExistingState) : EnqueueOutcome;

    public sealed record QueueFull : EnqueueOutcome;
}
