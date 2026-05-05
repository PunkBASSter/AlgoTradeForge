using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// In-memory <see cref="IAggregationJobRegistry"/>. Two indexes:
/// <list type="bullet">
///   <item><c>_byJobId</c> — every job ever submitted (until lazy retention eviction).</item>
///   <item><c>_activeByFeedId</c> — only active (queued/running) jobs. Removed at terminal
///         transition so duplicate-feed_id submissions after completion proceed.</item>
/// </list>
/// </summary>
/// <remarks>
/// Mutation paths take a single lock keyed by <c>feed_id</c> rather than a global lock — two
/// jobs targeting different feeds enqueue and update progress without contention. Reads
/// (<see cref="Get"/>, snapshot in handlers) are lock-free against the dictionaries.
/// </remarks>
public sealed class AggregationJobRegistry : IAggregationJobRegistry
{
    private readonly ConcurrentDictionary<string, AggregationJobRecord> _byJobId = new();
    private readonly ConcurrentDictionary<string, string> _activeByFeedId = new();
    // Reviewer Issue B5 — O(1) lookup of the most-recent terminal record per feed-id.
    // Invariant: at most one terminal record per feed-id at any time, because each fresh
    // enqueue evicts the prior terminal (TRD §6.5 path b/c). Set at MarkTerminal callsites
    // (OnCompleted/OnErrored/OnCancelled); cleared at EvictTerminalForFeedIdIfPresent.
    private readonly ConcurrentDictionary<string, string> _terminalByFeedId = new();
    private readonly ConcurrentDictionary<string, object> _feedLocks = new();

    private readonly TimeProvider _clock;
    private readonly TimeSpan _retention;

    public AggregationJobRegistry(IOptions<HistoryLoaderOptions> options, TimeProvider clock)
    {
        _clock = clock;
        _retention = TimeSpan.FromMinutes(options.Value.Aggregator.JobRetentionMinutes);
    }

    public EnqueueOutcome TryEnqueue(AggregationJob job, IAggregationJobQueue queue)
    {
        var feedLock = _feedLocks.GetOrAdd(job.OutcomeFeedId, _ => new object());
        lock (feedLock)
        {
            // Active-feed_id check — 423.
            if (_activeByFeedId.TryGetValue(job.OutcomeFeedId, out var existingJobId)
                && _byJobId.TryGetValue(existingJobId, out var existing)
                && existing.State.IsActive())
            {
                return new EnqueueOutcome.FeedAlreadyLocked(existing.Job.JobId, existing.State);
            }

            // Terminal-with-same-feed_id eviction (TRD §6.5 paths b/c of P1b-30):
            // any prior terminal record for this feed_id is evicted on fresh enqueue regardless
            // of its retention status. Within-retention → still evicted (path b); past-retention
            // would have been evicted already on the prior Get if anyone looked, but evict here
            // to be safe (path c).
            EvictTerminalForFeedIdIfPresent(job.OutcomeFeedId);

            // Queue write before recording: if the queue is full we want zero registry
            // mutation. Channel.TryWrite is non-blocking so this lock window stays short.
            if (!queue.TryWrite(job))
            {
                return new EnqueueOutcome.QueueFull();
            }

            var record = new AggregationJobRecord
            {
                Job = job,
                QueuedAt = _clock.GetUtcNow(),
            };
            record.QueuePosition = queue.CurrentDepth;
            record.AppendEvent(new ProgressEvent.Queued(
                job.JobId, job.OutcomeFeedId, record.QueuedAt, record.QueuePosition));

            _byJobId[job.JobId] = record;
            _activeByFeedId[job.OutcomeFeedId] = job.JobId;

            return new EnqueueOutcome.Accepted(record);
        }
    }

    public ActiveJobInfo? CheckActiveFeedId(string feedId)
    {
        if (_activeByFeedId.TryGetValue(feedId, out var jobId)
            && _byJobId.TryGetValue(jobId, out var record)
            && record.State.IsActive())
        {
            return new ActiveJobInfo(record.Job.JobId, record.State);
        }
        return null;
    }

    public AggregationJobRecord? Get(string jobId)
    {
        if (!_byJobId.TryGetValue(jobId, out var record))
            return null;

        // Lazy retention eviction: terminal records past the retention window vanish from
        // Get(). Caller distinguishes "never existed" from "evicted" by calling this after
        // the SSE consumer drops — for that the SSE handler keeps the record reference alive.
        if (record.State.IsTerminal()
            && record.CompletedAt is { } completedAt
            && _clock.GetUtcNow() - completedAt > _retention)
        {
            _byJobId.TryRemove(jobId, out _);
            return null;
        }
        return record;
    }

    public void OnStarted(string jobId, string sourceFeedId)
    {
        if (!_byJobId.TryGetValue(jobId, out var record)) return;
        var feedLock = _feedLocks.GetOrAdd(record.Job.OutcomeFeedId, _ => new object());
        lock (feedLock)
        {
            record.State = AggregationJobState.Running;
            record.StartedAt = _clock.GetUtcNow();
            record.AppendEvent(new ProgressEvent.Started(
                jobId, record.Job.OutcomeFeedId, record.StartedAt.Value, sourceFeedId));
        }
    }

    public void OnProgress(string jobId, string? currentPartition, long barsEmitted, long elapsedMs)
    {
        if (!_byJobId.TryGetValue(jobId, out var record)) return;
        var feedLock = _feedLocks.GetOrAdd(record.Job.OutcomeFeedId, _ => new object());
        lock (feedLock)
        {
            record.CurrentPartition = currentPartition;
            record.BarsEmitted = barsEmitted;
            record.AppendEvent(new ProgressEvent.Progress(
                jobId, currentPartition, barsEmitted, elapsedMs));
        }
    }

    public void OnCompleted(string jobId, AggregationResult result)
    {
        if (!_byJobId.TryGetValue(jobId, out var record)) return;
        var feedLock = _feedLocks.GetOrAdd(record.Job.OutcomeFeedId, _ => new object());
        lock (feedLock)
        {
            // Populate result/payload BEFORE flipping State so a snapshot reader (which takes
            // the record's events lock — see Snapshot()) never observes
            // (State == Complete && Result == null). State is the visibility anchor.
            record.MarkTerminal(
                state: AggregationJobState.Complete,
                completedAt: _clock.GetUtcNow(),
                result: result,
                error: null,
                barsEmitted: result.BarCount,
                terminalEvent: new ProgressEvent.Complete(result));
            // Drop active index — a fresh job for the same feed_id may now proceed.
            _activeByFeedId.TryRemove(record.Job.OutcomeFeedId, out _);
            _terminalByFeedId[record.Job.OutcomeFeedId] = jobId;
        }
    }

    public void OnErrored(string jobId, string code, string message, bool retryable)
    {
        if (!_byJobId.TryGetValue(jobId, out var record)) return;
        var feedLock = _feedLocks.GetOrAdd(record.Job.OutcomeFeedId, _ => new object());
        lock (feedLock)
        {
            var err = new ProgressEvent.Error(jobId, code, message, retryable);
            record.MarkTerminal(
                state: AggregationJobState.Error,
                completedAt: _clock.GetUtcNow(),
                result: null,
                error: err,
                barsEmitted: record.BarsEmitted,
                terminalEvent: err);
            _activeByFeedId.TryRemove(record.Job.OutcomeFeedId, out _);
            _terminalByFeedId[record.Job.OutcomeFeedId] = jobId;
        }
    }

    public CancelRequestOutcome TryRequestCancel(string jobId)
    {
        if (!_byJobId.TryGetValue(jobId, out var record))
            return new CancelRequestOutcome.Unknown();

        var feedLock = _feedLocks.GetOrAdd(record.Job.OutcomeFeedId, _ => new object());
        lock (feedLock)
        {
            // Re-check inside the lock — a concurrent retention eviction in Get() could have
            // pulled this record between TryGetValue and lock acquisition.
            if (!_byJobId.ContainsKey(jobId))
                return new CancelRequestOutcome.Unknown();

            var observed = record.State;
            if (observed.IsTerminal())
                return new CancelRequestOutcome.AlreadyTerminal(observed);

            // Fire the per-job CTS — pipeline observes at next ct.ThrowIfCancellationRequested().
            // Worker host's catch routes to OnCancelled. Cts is disposed inside MarkTerminal —
            // because we hold the per-feed-id lock and re-checked active state above, no other
            // thread can have flipped to terminal+disposed in this window.
            try { record.Cts.Cancel(); }
            catch (ObjectDisposedException) { return new CancelRequestOutcome.AlreadyTerminal(record.State); }
            return new CancelRequestOutcome.Requested();
        }
    }

    public void OnCancelled(string jobId, string reason)
    {
        if (!_byJobId.TryGetValue(jobId, out var record)) return;
        var feedLock = _feedLocks.GetOrAdd(record.Job.OutcomeFeedId, _ => new object());
        lock (feedLock)
        {
            // Idempotent: if a host_shutdown OnErrored beat us here, don't double-flip.
            if (record.State.IsTerminal()) return;

            var ev = new ProgressEvent.Cancelled(jobId, reason, _clock.GetUtcNow());
            record.MarkTerminal(
                state: AggregationJobState.Cancelled,
                completedAt: _clock.GetUtcNow(),
                result: null,
                error: null,
                barsEmitted: record.BarsEmitted,
                terminalEvent: ev,
                cancellationReason: reason);
            _activeByFeedId.TryRemove(record.Job.OutcomeFeedId, out _);
            _terminalByFeedId[record.Job.OutcomeFeedId] = jobId;
        }
    }

    public CancellationToken GetCancellationToken(string jobId)
    {
        if (!_byJobId.TryGetValue(jobId, out var record))
            return CancellationToken.None;
        // Cts is disposed inside MarkTerminal under the events lock. A worker that asks for
        // the token after the record went terminal (e.g. dequeue racing host shutdown that
        // already errored the job) would otherwise crash on Cts.Token. Mirrors the precedent
        // at TryRequestCancel above.
        try { return record.Cts.Token; }
        catch (ObjectDisposedException) { return CancellationToken.None; }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private void EvictTerminalForFeedIdIfPresent(string feedId)
    {
        // Reviewer Issue B5 — O(1) via the terminal-by-feed-id index. Invariant: at most one
        // terminal record per feed-id at any time (every fresh enqueue evicts the prior terminal
        // first, and no terminal-then-terminal transitions occur because the active index gates
        // re-entry). A stale pointer to an already-evicted record is harmless: TryRemove returns
        // false, and the index entry is reset on the next OnCompleted/OnErrored/OnCancelled.
        if (_terminalByFeedId.TryRemove(feedId, out var terminalJobId))
            _byJobId.TryRemove(terminalJobId, out _);
    }
}
