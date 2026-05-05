using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// In-memory <see cref="IAggregationJobRegistry"/>. Mutation paths use per-feed_id locks
/// rather than a global lock so jobs targeting different feeds don't contend. Three indexes:
/// <c>_byJobId</c> (all jobs until retention eviction), <c>_activeByFeedId</c> (queued/running,
/// removed on terminal), <c>_terminalByFeedId</c> (most recent terminal per feed, O(1) eviction).
/// Invariant: at most one terminal record per feed-id at any time — fresh enqueues evict the
/// prior terminal first, and the active index gates re-entry.
/// </summary>
public sealed class AggregationJobRegistry : IAggregationJobRegistry
{
    private readonly ConcurrentDictionary<string, AggregationJobRecord> _byJobId = new();
    private readonly ConcurrentDictionary<string, string> _activeByFeedId = new();
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
            // 423 — active job already owns this feed-id.
            if (_activeByFeedId.TryGetValue(job.OutcomeFeedId, out var existingJobId)
                && _byJobId.TryGetValue(existingJobId, out var existing)
                && existing.State.IsActive())
            {
                return new EnqueueOutcome.FeedAlreadyLocked(existing.Job.JobId, existing.State);
            }

            // Always evict any prior terminal record for this feed-id, regardless of retention.
            EvictTerminalForFeedIdIfPresent(job.OutcomeFeedId);

            // Queue first so a full queue leaves zero registry mutation.
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

        // Lazy retention eviction — terminal records past their retention window vanish here.
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
            // MarkTerminal populates payload before flipping State (visibility anchor) so
            // snapshot readers cannot observe (State==Complete && Result==null).
            record.MarkTerminal(
                state: AggregationJobState.Complete,
                completedAt: _clock.GetUtcNow(),
                result: result,
                error: null,
                barsEmitted: result.BarCount,
                terminalEvent: new ProgressEvent.Complete(result));
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
            // Re-check under lock — retention eviction in Get() could have run between
            // TryGetValue and lock acquisition.
            if (!_byJobId.ContainsKey(jobId))
                return new CancelRequestOutcome.Unknown();

            var observed = record.State;
            if (observed.IsTerminal())
                return new CancelRequestOutcome.AlreadyTerminal(observed);

            // The per-feed-id lock + active-state recheck guarantees Cts is not disposed here,
            // but defend against the race anyway.
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
            // Idempotent — if a host_shutdown OnErrored beat us here, don't double-flip.
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
        // Cts may have been disposed inside MarkTerminal if the record went terminal between
        // dequeue and this call (e.g. dequeue racing host shutdown).
        try { return record.Cts.Token; }
        catch (ObjectDisposedException) { return CancellationToken.None; }
    }

    private void EvictTerminalForFeedIdIfPresent(string feedId)
    {
        // O(1) via the terminal-by-feed-id index. A stale pointer is harmless: TryRemove
        // returns false and the index is repopulated on the next terminal transition.
        if (_terminalByFeedId.TryRemove(feedId, out var terminalJobId))
            _byJobId.TryRemove(terminalJobId, out _);
    }
}
