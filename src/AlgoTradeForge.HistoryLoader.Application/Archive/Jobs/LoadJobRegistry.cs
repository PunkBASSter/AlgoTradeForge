using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;

/// <summary>
/// In-memory registry for load jobs. Mutation paths use per-feedKey lock objects so jobs
/// targeting different feeds don't contend. Three indexes: <c>_byJobId</c> (all jobs until
/// retention eviction), <c>_activeByFeedKey</c> (queued/running; removed on terminal),
/// <c>_activeByAssetDir</c> (most recent active job per assetDir; drives ActiveJobForSymbol).
/// Snapshot polling only — no SSE/event log.
/// </summary>
public sealed class LoadJobRegistry : ILoadJobRegistry
{
    private readonly ConcurrentDictionary<string, LoadJobRecord> _byJobId = new();
    private readonly ConcurrentDictionary<string, string> _activeByFeedKey = new();
    private readonly ConcurrentDictionary<string, string> _activeByAssetDir = new();
    private readonly ConcurrentDictionary<string, object> _feedKeyLocks = new();

    private readonly Channel<LoadJob> _channel;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retention;

    public LoadJobRegistry(IOptions<HistoryLoaderOptions> options, TimeProvider clock)
    {
        _clock = clock;
        _retention = TimeSpan.FromMinutes(options.Value.Load.JobRetentionMinutes);
        _channel = Channel.CreateBounded<LoadJob>(options.Value.Load.MaxQueueDepth);
    }

    public LoadEnqueueOutcome TryEnqueue(LoadJob job, string feedKey)
    {
        var feedLock = _feedKeyLocks.GetOrAdd(feedKey, _ => new object());
        lock (feedLock)
        {
            // 423-equivalent: active job already owns this feed key.
            if (_activeByFeedKey.TryGetValue(feedKey, out var existingId)
                && _byJobId.TryGetValue(existingId, out var existing)
                && existing.State is LoadJobState.Queued or LoadJobState.Running)
            {
                return new LoadEnqueueOutcome.FeedBusy(existingId);
            }

            var record = new LoadJobRecord
            {
                FeedKey = feedKey,
                Job = job,
                QueuedAt = _clock.GetUtcNow(),
            };

            // Publish the record BEFORE the channel write: the bounded channel wakes the
            // waiting worker on TryWrite, and its OnStarted(jobId) must find the record
            // (happens-before via the channel). The feed-key lock then orders OnStarted's
            // state flip after the index writes below. Rollback keeps a full queue at
            // zero net registry mutation; the unpublished jobId makes the transient
            // record unobservable.
            _byJobId[job.JobId] = record;
            if (!_channel.Writer.TryWrite(job))
            {
                _byJobId.TryRemove(job.JobId, out _);
                return new LoadEnqueueOutcome.QueueFull();
            }

            _activeByFeedKey[feedKey] = job.JobId;
            _activeByAssetDir[AssetDirFromFeedKey(feedKey)] = job.JobId;

            return new LoadEnqueueOutcome.Accepted(record);
        }
    }

    public LoadJobSnapshot? Get(string jobId)
    {
        if (!_byJobId.TryGetValue(jobId, out var record))
            return null;

        // Lazy retention eviction — terminal records past their retention window vanish here.
        if (record.State is LoadJobState.Complete or LoadJobState.Error
            && record.CompletedAt is { } completedAt
            && _clock.GetUtcNow() - completedAt > _retention)
        {
            _byJobId.TryRemove(jobId, out _);
            return null;
        }

        return record.Snapshot();
    }

    public string? ActiveJobForSymbol(string assetDir)
    {
        if (!_activeByAssetDir.TryGetValue(assetDir, out var jobId))
            return null;
        if (!_byJobId.TryGetValue(jobId, out var record))
            return null;
        return record.State is LoadJobState.Queued or LoadJobState.Running ? jobId : null;
    }

    public async Task<LoadJob?> Dequeue(CancellationToken ct)
    {
        try
        {
            return await _channel.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException) { return null; }
    }

    public void OnStarted(string jobId)
    {
        if (!_byJobId.TryGetValue(jobId, out var record)) return;
        // The worker's dequeue can race the tail of TryEnqueue (channel write precedes the
        // index writes); taking the feed-key lock orders this transition after TryEnqueue's
        // bookkeeping completes.
        var feedLock = _feedKeyLocks.GetOrAdd(record.FeedKey, _ => new object());
        lock (feedLock)
        {
            record.State = LoadJobState.Running;
        }
    }

    public void OnProgress(string jobId, int monthsDone, int monthsTotal, string currentMonth)
    {
        if (!_byJobId.TryGetValue(jobId, out var record)) return;
        var feedLock = _feedKeyLocks.GetOrAdd(record.FeedKey, _ => new object());
        lock (feedLock)
        {
            record.SetProgress(monthsDone, monthsTotal, currentMonth);
        }
    }

    public void OnCompleted(string jobId)
    {
        if (!_byJobId.TryGetValue(jobId, out var record)) return;
        MarkTerminal(record, jobId, LoadJobState.Complete, null, null);
    }

    public void OnErrored(string jobId, string code, string message)
    {
        if (!_byJobId.TryGetValue(jobId, out var record)) return;
        MarkTerminal(record, jobId, LoadJobState.Error, code, message);
    }

    private void MarkTerminal(LoadJobRecord record, string jobId, LoadJobState state, string? code, string? message)
    {
        var feedLock = _feedKeyLocks.GetOrAdd(record.FeedKey, _ => new object());
        lock (feedLock)
        {
            record.MarkTerminal(state, _clock.GetUtcNow(), code, message);
            _activeByFeedKey.TryRemove(record.FeedKey, out _);
            // Only remove the assetDir entry if it still points to this job — a newer job for
            // the same assetDir may have already claimed the slot.
            var assetDir = AssetDirFromFeedKey(record.FeedKey);
            _activeByAssetDir.TryRemove(KeyValuePair.Create(assetDir, jobId));
        }
    }

    // feedKey = $"{assetDir}|{feedName}|{interval}" — assetDir cannot contain '|' (it's a path).
    private static string AssetDirFromFeedKey(string feedKey) =>
        feedKey[..feedKey.IndexOf('|')];
}
