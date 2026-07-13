using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class LoadJobWorkerTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-load-worker-").FullName;
    private SqliteHistoryIndex _index = null!;
    private readonly JobWakeupQueue _wakeup = new(64);
    private readonly FakeArchiveLoadService _fakeArchiveLoad = new();
    private readonly CollectionPlanHolder _plan = new();
    private readonly JobCancellationMap _cancellations = new();
    private LoadJobWorker _worker = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");

        _plan.Publish(new CollectionPlan(
            [new CollectionAsset("binance", "BTC/USDT-PERP",
                new VenueInstrument("BTCUSDT", "CryptoPerpetual", "BTCUSDT_perp"), 2, [])],
            [], []));

        _worker = BuildWorker(_index);
    }

    private LoadJobWorker BuildWorker(IHistoryIndex index)
    {
        var sinkFactory = new JobProgressSinkFactory(index, new JobEventSignal());
        return new LoadJobWorker(
            _wakeup, index, _fakeArchiveLoad, sinkFactory, _cancellations,
            new LoadRequestRehydrator(_plan), NullLogger<LoadJobWorker>.Instance);
    }

    private async Task<string> EnqueueLoad(string symbol, string feedKeySuffix, string interval = "1m")
    {
        var reqJson = LoadRequestRehydrator.Serialize(
            "binance", symbol, "CryptoPerpetual", "candles", interval,
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 1));
        var outcome = await _index.TryAcquireFeedGate(
            "load", $"binance|{feedKeySuffix}|candles|{interval}", "{}", reqJson, Ct);
        return ((FeedGateOutcome.Acquired)outcome).JobId;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Worker_RehydratesQueuedRow_RunsService_ReachesComplete()
    {
        var jobId = await EnqueueLoad("BTCUSDT", "BTCUSDT_perp");

        _wakeup.TryEnqueue(jobId);
        await _worker.DrainOnceForTest(Ct); // rehydrate + run (fake IArchiveLoadService returns true)

        Assert.Equal("complete", (await _index.GetJob(jobId, Ct))!.State);
        Assert.Equal("1m", _fakeArchiveLoad.LastRequest!.Interval); // rehydration carried interval + dates
    }

    [Fact]
    public void SeedFromQueued_EnqueuesAllQueuedRows() =>
        Assert.Equal(2, _wakeup.SeedFromQueued(new[] { "a", "b" }));

    // Critical #1: a user DELETE trips the linked (per-job) token; the worker must record the
    // terminal state as 'cancelled' via sink.Cancel, NOT error/load_failed. Pre-fix the only catch
    // was `when (!IsTrueShutdown(...))`, and the linked-token OCE never satisfies IsTrueShutdown, so
    // every user cancel fell into the generic arm → Fail("load_failed"). (Fails pre-fix.)
    [Fact]
    public async Task Worker_UserCancel_TripsLinkedToken_RecordsCancelled()
    {
        var jobId = await EnqueueLoad("BTCUSDT", "BTCUSDT_perp");

        _fakeArchiveLoad.Behavior = async (_, _, ct) =>
        {
            // Mirror ArchiveLoadService: block until the linked token trips, then surface an OCE
            // carrying that same linked token (Task.Delay throws OperationCanceledException(ct)).
            await Task.Delay(Timeout.Infinite, ct);
            return true; // unreachable
        };

        _wakeup.TryEnqueue(jobId);
        var drive = _worker.DrainOnceForTest(Ct);

        await _fakeArchiveLoad.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _cancellations.Trip(jobId); // simulate DELETE tripping the per-job token
        await drive.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("cancelled", (await _index.GetJob(jobId, Ct))!.State);
        var events = await _index.GetJobEventsAfter(jobId, 0, Ct);
        var cancelEvent = Assert.Single(events, e => e.Kind == "cancelled");
        Assert.Contains("user_cancelled", cancelEvent.PayloadJson);
    }

    // M3.4-M4 cancel-while-queued race: a DELETE that arrives while the job sits queued sets the
    // durable cancel_requested flag but cannot Trip a per-job token (not yet Registered). The worker
    // must re-check after dequeue and short-circuit to 'cancelled' WITHOUT running the service or
    // entering 'running'. Non-vacuous: pre-fix the worker would call Run → state complete, Run called.
    [Fact]
    public async Task Worker_CancelRequestedWhileQueued_SkipsRun_RecordsCancelled()
    {
        var jobId = await EnqueueLoad("BTCUSDT", "BTCUSDT_perp");
        await _index.RequestCancel(jobId, Ct); // durable flag only — job never Registered, so no Trip

        _wakeup.TryEnqueue(jobId);
        await _worker.DrainOnceForTest(Ct);

        Assert.Equal("cancelled", (await _index.GetJob(jobId, Ct))!.State);
        Assert.Null(_fakeArchiveLoad.LastRequest); // the load service was never run
        var cancelEvent = Assert.Single(await _index.GetJobEventsAfter(jobId, 0, Ct), e => e.Kind == "cancelled");
        Assert.Contains("user_cancelled", cancelEvent.PayloadJson);
    }

    // Important #2: a transient GetJob read must not fault the BackgroundService (→ StopHost → all
    // collectors down). Pre-fix GetJob ran OUTSIDE the per-item try, so a throw propagated out of the
    // drain loop and item 2 never ran. (Fails pre-fix: item 2 never reaches 'complete'.)
    [Fact]
    public async Task Worker_JobReadThrows_DoesNotKillDrainLoop()
    {
        var badJob = await EnqueueLoad("BTCUSDT", "bad_perp");
        var goodJob = await EnqueueLoad("BTCUSDT", "good_perp");

        var worker = BuildWorker(new ThrowOnGetJobIndex(_index, badJob));

        _wakeup.TryEnqueue(badJob);  // GetJob throws for this one
        _wakeup.TryEnqueue(goodJob); // must still be drained to completion
        await worker.DrainForTest(2, Ct);

        Assert.Equal("complete", (await _index.GetJob(goodJob, Ct))!.State);
    }

    // Rehydrating a row whose symbol is no longer in the plan throws InvalidOperationException; the
    // worker must Fail the job (load_failed) and keep draining — not crash the host.
    [Fact]
    public async Task Worker_RehydrateRemovedSymbol_FailsGracefully()
    {
        var jobId = await EnqueueLoad("ETHUSDT", "ETHUSDT_perp"); // ETHUSDT is not in the published plan

        _wakeup.TryEnqueue(jobId);
        await _worker.DrainOnceForTest(Ct);

        var row = (await _index.GetJob(jobId, Ct))!;
        Assert.Equal("error", row.State);
        Assert.Contains("load_failed", row.Error);
    }

    private sealed class FakeArchiveLoadService : IArchiveLoadService
    {
        public ArchiveLoadRequest? LastRequest { get; private set; }
        public Func<ArchiveLoadRequest, IJobProgressSink, CancellationToken, Task<bool>>? Behavior { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> Run(ArchiveLoadRequest req, IJobProgressSink sink, CancellationToken ct = default)
        {
            LastRequest = req;
            Started.TrySetResult();
            if (Behavior is not null)
                return await Behavior(req, sink, ct);

            // Run owns the terminal transition (mirrors the real ArchiveLoadService success path).
            await sink.Complete("""{"ok":true}""", ct);
            return true;
        }
    }

    // Forces GetJob to throw for one job id (simulating a transient SQLITE_BUSY on the durable read);
    // every other member delegates to the real index so the surviving job runs end-to-end.
    private sealed class ThrowOnGetJobIndex(IHistoryIndex inner, string throwForJobId) : IHistoryIndex
    {
        public Task<IndexJobRow?> GetJob(string id, CancellationToken ct = default) =>
            id == throwForJobId
                ? throw new SqliteException("database is locked", 5)
                : inner.GetJob(id, ct);

        public Task UpsertAsset(AssetIndexRow row, CancellationToken ct = default) => inner.UpsertAsset(row, ct);
        public Task RemoveAsset(string exchange, string dir, CancellationToken ct = default) => inner.RemoveAsset(exchange, dir, ct);
        public Task<IReadOnlyList<AssetIndexRow>> ListAssets(string? exchange = null, CancellationToken ct = default) => inner.ListAssets(exchange, ct);
        public Task<AssetIndexRow?> GetAsset(string exchange, string dir, CancellationToken ct = default) => inner.GetAsset(exchange, dir, ct);
        public Task UpsertFeedStatus(FeedStatusIndexRow row, CancellationToken ct = default) => inner.UpsertFeedStatus(row, ct);
        public Task<IReadOnlyList<FeedStatusIndexRow>> GetFeedStatuses(string exchange, string dir, CancellationToken ct = default) => inner.GetFeedStatuses(exchange, dir, ct);
        public Task ReplaceMonths(string exchange, string dir, string feedName, string interval, IReadOnlyList<MonthPartitionRow> months, CancellationToken ct = default) => inner.ReplaceMonths(exchange, dir, feedName, interval, months, ct);
        public Task<IReadOnlyList<MonthPartitionRow>> GetMonths(string exchange, string dir, string feedName, string interval, CancellationToken ct = default) => inner.GetMonths(exchange, dir, feedName, interval, ct);
        public Task DeleteMonthPartition(string exchange, string dir, string feedName, string interval, string month, CancellationToken ct = default) => inner.DeleteMonthPartition(exchange, dir, feedName, interval, month, ct);
        public Task RemoveCompleteMonth(string exchange, string dir, string feedName, string interval, string month, CancellationToken ct = default) => inner.RemoveCompleteMonth(exchange, dir, feedName, interval, month, ct);
        public Task<IReadOnlyList<(string FeedName, string Interval)>> ListFeedKeys(string exchange, string dir, CancellationToken ct = default) => inner.ListFeedKeys(exchange, dir, ct);
        public Task UpsertInstrumentMeta(IReadOnlyList<InstrumentMetaRow> rows, CancellationToken ct = default) => inner.UpsertInstrumentMeta(rows, ct);
        public Task<IReadOnlyList<InstrumentMetaRow>> ListInstrumentMeta(string? exchange = null, CancellationToken ct = default) => inner.ListInstrumentMeta(exchange, ct);
        public Task SetDiscoveredFirstMonth(string exchange, string dir, string feedName, string interval, string month, CancellationToken ct = default) => inner.SetDiscoveredFirstMonth(exchange, dir, feedName, interval, month, ct);
        public Task<IReadOnlyList<DiscoveredFirstMonthRow>> ListDiscoveredFirstMonths(CancellationToken ct = default) => inner.ListDiscoveredFirstMonths(ct);
        public Task<IReadOnlyList<(string Exchange, string Dir, string FeedName, string Interval)>> ListAllFeedKeys(CancellationToken ct = default) => inner.ListAllFeedKeys(ct);
        public Task PruneFeedData(string exchange, string dir, IReadOnlyCollection<(string FeedName, string Interval)> keep, CancellationToken ct = default) => inner.PruneFeedData(exchange, dir, keep, ct);
        public Task PruneAssetsNotIn(IReadOnlyCollection<(string Exchange, string Dir)> keep, CancellationToken ct = default) => inner.PruneAssetsNotIn(keep, ct);
        public Task<bool> IsEmpty(CancellationToken ct = default) => inner.IsEmpty(ct);
        public Task<FeedGateOutcome> TryAcquireFeedGate(string kind, string feedKey, string progressJson, string requestJson, CancellationToken ct = default) => inner.TryAcquireFeedGate(kind, feedKey, progressJson, requestJson, ct);
        public Task<string> CreateJob(string kind, CancellationToken ct = default) => inner.CreateJob(kind, ct);
        public Task UpdateJob(string id, string state, string? progressJson = null, string? error = null, CancellationToken ct = default) => inner.UpdateJob(id, state, progressJson, error, ct);
        public Task<IReadOnlyList<IndexJobRow>> ListJobs(string? kind, string? state, CancellationToken ct = default) => inner.ListJobs(kind, state, ct);
        public Task<IndexJobRow?> GetActiveJob(string kind, CancellationToken ct = default) => inner.GetActiveJob(kind, ct);
        public Task<IndexJobRow?> GetLastJob(string kind, CancellationToken ct = default) => inner.GetLastJob(kind, ct);
        public Task<int> AppendJobEvent(string jobId, string eventKind, string payloadJson, CancellationToken ct = default) => inner.AppendJobEvent(jobId, eventKind, payloadJson, ct);
        public Task<IReadOnlyList<JobEventRow>> GetJobEventsAfter(string jobId, int afterSeq, CancellationToken ct = default) => inner.GetJobEventsAfter(jobId, afterSeq, ct);
        public Task<int> GetLastEventSeq(string jobId, CancellationToken ct = default) => inner.GetLastEventSeq(jobId, ct);
        public Task RequestCancel(string jobId, CancellationToken ct = default) => inner.RequestCancel(jobId, ct);
        public Task SetTouched(string jobId, string feedKey, string month, CancellationToken ct = default) => inner.SetTouched(jobId, feedKey, month, ct);
        public Task<IReadOnlyList<InterruptedJobRow>> ListInterruptedJobs(CancellationToken ct = default) => inner.ListInterruptedJobs(ct);
        public Task DeleteJob(string jobId, CancellationToken ct = default) => inner.DeleteJob(jobId, ct);
        public Task<int> DeleteTerminalJobsBefore(DateTimeOffset cutoffUtc, CancellationToken ct = default) => inner.DeleteTerminalJobsBefore(cutoffUtc, ct);
    }
}
