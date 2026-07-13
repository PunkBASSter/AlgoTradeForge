using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.WebApi.Aggregation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// End-to-end drain tests for the store-backed aggregation worker pools: rehydration to
/// completion, host-owned user-cancel classification, and per-item fault isolation. Mirrors
/// <c>LoadJobWorkerTests</c> — the load path's proven reference for these three concerns.
/// </summary>
public sealed class AggregationWorkerHostTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-agg-worker-").FullName;
    private SqliteHistoryIndex _index = null!;
    private readonly JobWakeupQueue _timeBarWakeup = new(64);
    private readonly JobWakeupQueue _tickWakeup = new(64);
    private readonly FakeAggregationService _fakeService = new();
    private readonly JobCancellationMap _cancellations = new();
    private readonly AggregationRequestRehydrator _rehydrator = new();
    private AggregationWorkerHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");
        _host = BuildHost(_index);
    }

    private AggregationWorkerHost BuildHost(IHistoryIndex index) =>
        new(_timeBarWakeup, _tickWakeup, index, _fakeService,
            new JobProgressSinkFactory(index, new JobEventSignal()),
            _cancellations, _rehydrator, NullLogger<AggregationWorkerHost>.Instance);

    private static AggregationJob JobFor(string feedId) => new(
        JobId: "placeholder",
        Source: new DataFeedDescriptor("/data", "binance", "BTCUSDT", "1m", DataFeedKind.TimeBar),
        AssetDir: "/data/binance/BTCUSDT",
        OutcomeFeedId: feedId,
        TypeCode: "EqV",
        ThresholdAbsolute: 1000m,
        ThresholdScaled: 1000,
        ThresholdUnit: "base_asset",
        ThresholdInputMode: "absolute",
        ThresholdConvenienceInput: null,
        SourceScale: new ScaleContext(0.01m),
        AccumulatorScale: new ScaleContext(0.01m),
        MaxPartitionSizeMB: 1,
        ToolVersion: "test-1.0");

    private async Task<string> EnqueueAggregation(string feedId)
    {
        var reqJson = AggregationRequestRehydrator.Serialize(JobFor(feedId), decimalDigits: 2);
        var outcome = await _index.TryAcquireFeedGate(
            "aggregation", $"binance|BTCUSDT|{feedId}|", "{}", reqJson, Ct);
        return ((FeedGateOutcome.Acquired)outcome).JobId;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // Step-1 acceptance: a queued row woken on the time-bar pool rehydrates and runs to complete
    // (the fake service owns the terminal Complete on the happy path, like the real one).
    [Fact]
    public async Task AggHost_RehydratesQueuedJob_Completes()
    {
        var jobId = await EnqueueAggregation("EqV_1m_1000");

        _timeBarWakeup.TryEnqueue(jobId);
        await _host.DrainOnceForTest(Ct);

        Assert.Equal("complete", (await _index.GetJob(jobId, Ct))!.State);
        Assert.Equal("EqV_1m_1000", _fakeService.LastJob!.OutcomeFeedId); // rehydration carried the feed id
    }

    // A user DELETE trips the linked (per-job) token; the HOST must record the terminal state as
    // 'cancelled' via sink.Cancel("user_cancelled"), NOT error — the service lets the OCE
    // propagate (D2: host owns classification).
    [Fact]
    public async Task AggHost_UserCancel_TripsLinkedToken_RecordsCancelled()
    {
        var jobId = await EnqueueAggregation("EqV_1m_1000");

        _fakeService.Behavior = async (_, _, ct) =>
        {
            // Block until the linked token trips, then surface an OCE carrying that same token.
            await Task.Delay(Timeout.Infinite, ct);
        };

        _timeBarWakeup.TryEnqueue(jobId);
        var drive = _host.DrainOnceForTest(Ct);

        await _fakeService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _cancellations.Trip(jobId); // simulate DELETE tripping the per-job token
        await drive.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("cancelled", (await _index.GetJob(jobId, Ct))!.State);
        var events = await _index.GetJobEventsAfter(jobId, 0, Ct);
        var cancelEvent = Assert.Single(events, e => e.Kind == "cancelled");
        Assert.Contains("user_cancelled", cancelEvent.PayloadJson);
    }

    // M3.4-M4 cancel-while-queued race (mirror of the load path): a DELETE arriving while the job
    // sits queued sets cancel_requested but cannot Trip a per-job token. The host must re-check after
    // dequeue and short-circuit to 'cancelled' WITHOUT running the service or entering 'running'.
    [Fact]
    public async Task AggHost_CancelRequestedWhileQueued_SkipsRun_RecordsCancelled()
    {
        var jobId = await EnqueueAggregation("EqV_1m_1000");
        await _index.RequestCancel(jobId, Ct); // durable flag only — job never Registered, so no Trip

        _timeBarWakeup.TryEnqueue(jobId);
        await _host.DrainOnceForTest(Ct);

        Assert.Equal("cancelled", (await _index.GetJob(jobId, Ct))!.State);
        Assert.Null(_fakeService.LastJob); // the aggregation service was never run
        var cancelEvent = Assert.Single(await _index.GetJobEventsAfter(jobId, 0, Ct), e => e.Kind == "cancelled");
        Assert.Contains("user_cancelled", cancelEvent.PayloadJson);
    }

    // Per-item isolation: a transient GetJob read fault must not fault the pool loop. GetJob runs
    // INSIDE the per-item try, so a throw is swallowed and the next queued job still completes.
    [Fact]
    public async Task AggHost_JobReadThrows_DoesNotKillDrainLoop()
    {
        var badJob = await EnqueueAggregation("EqV_1m_1000");
        var goodJob = await EnqueueAggregation("EqV_1m_2000");

        var host = BuildHost(new ThrowOnGetJobIndex(_index, badJob));

        _timeBarWakeup.TryEnqueue(badJob);  // GetJob throws for this one
        _timeBarWakeup.TryEnqueue(goodJob); // must still be drained to completion
        await host.DrainForTest(2, Ct);

        Assert.Equal("complete", (await _index.GetJob(goodJob, Ct))!.State);
    }

    private sealed class FakeAggregationService : IAggregationService
    {
        public AggregationJob? LastJob { get; private set; }
        public Func<AggregationRunRequest, IJobProgressSink, CancellationToken, Task>? Behavior { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Run(AggregationRunRequest req, IJobProgressSink sink, CancellationToken ct = default)
        {
            LastJob = req.Job;
            Started.TrySetResult();
            if (Behavior is not null)
            {
                await Behavior(req, sink, ct);
                return;
            }

            // Run owns the terminal transition (mirrors the real AggregationService happy path).
            await sink.Complete("""{"ok":true}""", ct);
        }
    }

    // Forces GetJob to throw for one job id (simulating a transient SQLITE_BUSY on the durable
    // read); every other member delegates to the real index so the surviving job runs end-to-end.
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
