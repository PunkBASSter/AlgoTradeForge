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

    /// <summary>
    /// Phase 6 — request cooperative cancellation of an active (queued or running) job.
    /// Fires the per-job <see cref="System.Threading.CancellationTokenSource"/> vended to the
    /// worker on dequeue; the worker observes <see cref="System.OperationCanceledException"/>
    /// at its next per-record check and routes to <see cref="OnCancelled"/>.
    /// </summary>
    /// <remarks>
    /// Tri-state outcome (Reviewer Issue B1): the caller cannot otherwise distinguish "job
    /// never existed / retention-expired" from "job already terminal" using a single bool +
    /// out-state — a concurrent retention eviction in the gap between an existence-probe and
    /// the cancel call would cause the bool path to misreport "terminal in state Queued".
    /// Returning a discriminated outcome closes the race without an extra dictionary lookup
    /// at the endpoint.
    /// </remarks>
    CancelRequestOutcome TryRequestCancel(string jobId);

    /// <summary>
    /// Worker entry point after cancellation is observed. Mirrors <see cref="OnErrored"/>'s
    /// state-transition + retention behavior; appends a <see cref="ProgressEvent.Cancelled"/>
    /// terminal event and clears the active-feed_id index.
    /// </summary>
    void OnCancelled(string jobId, string reason);

    /// <summary>
    /// Worker dequeue path retrieves the per-job <see cref="System.Threading.CancellationToken"/>
    /// to combine with the host stopping token. Returns <see cref="System.Threading.CancellationToken.None"/>
    /// for unknown jobs (defense-in-depth — worker should always have a valid record).
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
    /// <summary>Cancel observed at the registry — per-job CTS fired. Worker will land
    /// <see cref="IAggregationJobRegistry.OnCancelled"/> at the next per-record cancellation
    /// checkpoint. NOT a guarantee the run was aborted: if the pipeline emitted its last record
    /// in the gap between this call and the worker's next check, OnCompleted may still win and
    /// the SSE terminal event will be <c>complete</c>. The FE reconciles via SSE.</summary>
    public sealed record Requested : CancelRequestOutcome;

    /// <summary>No record exists for the given jobId (never submitted, or retention-evicted).</summary>
    public sealed record Unknown : CancelRequestOutcome;

    /// <summary>Job already in a terminal state — Complete, Error, or Cancelled.</summary>
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
