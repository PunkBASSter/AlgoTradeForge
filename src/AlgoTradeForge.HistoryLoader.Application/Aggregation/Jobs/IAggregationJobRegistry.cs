namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// In-memory registry of aggregation jobs. Owns the per-<c>feed_id</c> active-job index
/// (drives the 423 dedup check) and the per-job event log (drives SSE replay).
/// </summary>
public interface IAggregationJobRegistry
{
    /// <summary>
    /// Atomically enqueues a new job: checks active-feed_id (423), evicts any prior terminal
    /// for the same feed_id, pushes onto the queue, then records the job.
    /// </summary>
    EnqueueOutcome TryEnqueue(AggregationJob job, IAggregationJobQueue queue);

    /// <summary>Gets a job by id. Terminal records past retention return <c>null</c>.</summary>
    AggregationJobRecord? Get(string jobId);

    /// <summary>
    /// Returns the active (queued/running) job for a feed-id, or <c>null</c>. Used by
    /// <c>POST /aggregate</c> to enforce 423 → 409 precedence before <see cref="TryEnqueue"/>.
    /// </summary>
    ActiveJobInfo? CheckActiveFeedId(string feedId);

    void OnStarted(string jobId, string sourceFeedId);
    void OnProgress(string jobId, string? currentPartition, long barsEmitted, long elapsedMs);
    void OnCompleted(string jobId, AggregationResult result);
    void OnErrored(string jobId, string code, string message, bool retryable);

    /// <summary>
    /// Requests cooperative cancellation of an active job. Fires the per-job CTS; the worker
    /// observes <see cref="System.OperationCanceledException"/> at its next checkpoint and
    /// routes to <see cref="OnCancelled"/>. Tri-state outcome avoids an existence-probe race
    /// where retention eviction in the gap would otherwise misreport state.
    /// </summary>
    CancelRequestOutcome TryRequestCancel(string jobId);

    /// <summary>
    /// Worker entry point after cancellation is observed. Appends a
    /// <see cref="ProgressEvent.Cancelled"/> terminal event and clears the active-feed_id index.
    /// </summary>
    void OnCancelled(string jobId, string reason);

    /// <summary>
    /// Per-job <see cref="CancellationToken"/> for the worker to combine with the host
    /// stopping token. Returns <see cref="CancellationToken.None"/> for unknown jobs.
    /// </summary>
    CancellationToken GetCancellationToken(string jobId);
}

public sealed record ActiveJobInfo(string JobId, AggregationJobState State);

/// <summary>
/// Tri-state result of <see cref="IAggregationJobRegistry.TryRequestCancel"/>. Endpoint maps
/// <see cref="Requested"/> → 204, <see cref="Unknown"/> → 404, <see cref="AlreadyTerminal"/> → 409.
/// </summary>
public abstract record CancelRequestOutcome
{
    /// <summary>
    /// Per-job CTS fired. NOT a guarantee the run was aborted: if the pipeline emitted its
    /// last record in the gap before the worker's next check, OnCompleted may still win and
    /// the SSE terminal event will be <c>complete</c>. The FE reconciles via SSE.
    /// </summary>
    public sealed record Requested : CancelRequestOutcome;

    /// <summary>No record exists (never submitted, or retention-evicted).</summary>
    public sealed record Unknown : CancelRequestOutcome;

    /// <summary>Job already in a terminal state.</summary>
    public sealed record AlreadyTerminal(AggregationJobState State) : CancelRequestOutcome;
}

public abstract record EnqueueOutcome
{
    public sealed record Accepted(AggregationJobRecord Record) : EnqueueOutcome;

    public sealed record FeedAlreadyLocked(
        string ExistingJobId,
        AggregationJobState ExistingState) : EnqueueOutcome;

    public sealed record QueueFull : EnqueueOutcome;
}
