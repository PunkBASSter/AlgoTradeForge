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
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private void EvictTerminalForFeedIdIfPresent(string feedId)
    {
        // Slow scan, but the registry is small (few hundred entries at peak) and this happens
        // only on enqueue. If profiling shows this hot, swap to a secondary terminal-by-feedId index.
        foreach (var kvp in _byJobId)
        {
            if (kvp.Value.State.IsTerminal()
                && string.Equals(kvp.Value.Job.OutcomeFeedId, feedId, StringComparison.Ordinal))
            {
                _byJobId.TryRemove(kvp.Key, out _);
            }
        }
    }
}
