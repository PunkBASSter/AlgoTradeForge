using System.Text.Json;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.WebApi.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Jobs;

/// <summary>
/// End-to-end drain tests for the store-backed materialize worker: sequential-stage completion,
/// resume-from-stage (skips already-done stages), §S7 interrupted-reseed, and host-owned user-cancel.
/// The stage-request construction is stubbed (FakeStageRequestFactory) so these tests exercise the
/// worker's orchestration — the stage loop, canonical progress round-trip (done=stage index), and composite terminal.
/// </summary>
public sealed class MaterializeWorkerHostTests : IAsyncLifetime, IDisposable
{
    private static readonly JsonSerializerOptions _snake =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly string _dir = Directory.CreateTempSubdirectory("atf-mat-worker-").FullName;
    private SqliteHistoryIndex _index = null!;
    private readonly JobWakeupQueue _wakeup = new(64);
    private readonly FakeArchiveLoadService _fakeLoad = new();
    private readonly FakeAggregationService _fakeAgg = new();
    private readonly CollectionPlanHolder _plan = new();
    private readonly JobCancellationMap _cancellations = new();
    private MaterializeWorkerHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VenueInstrument _venue = new("BTCUSDT", "perpetual", "BTCUSDT_perp");

    // Plan with one on-demand tick feed "agg-trades" + one derived feed "EqV_1k" sourced from it,
    // so MaterializePlan.Resolve("EqV_1k") yields [Load(agg-trades), Aggregate(EqV_1k)].
    private static readonly CollectionPlan _collectionPlan = new(
        Assets:
        [
            new CollectionAsset("binance", "BTC/USDT-PERP", _venue, 2,
                [new CollectionFeed("agg-trades", "", "on-demand", "csv", new DateOnly(2023, 1, 1))])
        ],
        Blocked: [],
        Warnings: [])
    {
        Derived = [new DerivedFeedEntry("binance", "BTC/USDT-PERP", _venue, "EqV_1k", "agg-trades")],
    };

    public async ValueTask InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");
        _plan.Publish(_collectionPlan);
        _host = BuildHost(_index);
    }

    private MaterializeWorkerHost BuildHost(IHistoryIndex index) =>
        new(_wakeup, index, _fakeLoad, _fakeAgg,
            new JobProgressSinkFactory(index, new JobEventSignal()),
            _cancellations, _plan, new FakeStageRequestFactory(),
            NullLogger<MaterializeWorkerHost>.Instance);

    // Writes a materialize row with the canonical snake_case progress_json
    // ({phase, done, total, detail:{stage_index, stages_total}}; done=stage index) + a
    // request_json the worker re-Resolves against the published plan.
    private async Task<string> SeedMaterializeJob(int stagesTotal, int stageIndex, string feedKey)
    {
        var progressJson = JsonSerializer.Serialize(
            new
            {
                phase = "load",
                done = stageIndex,
                total = stagesTotal,
                detail = new { stage_index = stageIndex, stages_total = stagesTotal },
            }, _snake);
        var reqJson = JsonSerializer.Serialize(
            new { exchange = "binance", symbol = "BTCUSDT", feed = "EqV_1k" }, _snake);
        var outcome = await _index.TryAcquireFeedGate("materialize", feedKey, progressJson, reqJson, Ct);
        return ((FeedGateOutcome.Acquired)outcome).JobId;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Materialize_RunsBothStages_Completes()
    {
        var jobId = await SeedMaterializeJob(stagesTotal: 2, stageIndex: 0, feedKey: "binance|BTCUSDT|EqV_1k|");
        _wakeup.TryEnqueue(jobId);
        await _host.DrainOnceForTest(Ct);

        Assert.Equal("complete", (await _index.GetJob(jobId, Ct))!.State);
        Assert.True(_fakeLoad.Ran && _fakeAgg.Ran);
    }

    // Non-vacuous: the worker MUST read done=1 (canonical progress) and skip the Load stage. If it
    // read the wrong key it would get 0 and run Load — failing the _fakeLoad.Ran==false assertion.
    [Fact]
    public async Task Materialize_ResumedAtStage1_SkipsLoad()
    {
        var jobId = await SeedMaterializeJob(stagesTotal: 2, stageIndex: 1, feedKey: "binance|BTCUSDT|EqV_1k|");
        _wakeup.TryEnqueue(jobId);
        await _host.DrainOnceForTest(Ct);

        Assert.False(_fakeLoad.Ran);
        Assert.True(_fakeAgg.Ran);
        Assert.Equal("complete", (await _index.GetJob(jobId, Ct))!.State);
    }

    // §S7: nothing else re-triggers a crashed composite, so the worker reseeds 'interrupted' rows —
    // resetting them to 'queued' FIRST, then enqueuing — so they resume at their persisted stage_index.
    [Fact]
    public async Task Boot_ReseedsInterruptedMaterialize()
    {
        var jobId = await SeedMaterializeJob(stagesTotal: 2, stageIndex: 1, feedKey: "binance|BTCUSDT|EqV_1k|");
        await _index.UpdateJob(jobId, "interrupted", ct: Ct);

        var n = await _host.SeedOnBootForTest(Ct);

        Assert.Equal(1, n);
        Assert.Equal("queued", (await _index.GetJob(jobId, Ct))!.State);
    }

    // A user DELETE trips the linked per-job token mid-composite; the HOST records the terminal
    // state as 'cancelled' (mirror of the M3 workers' cancel test).
    [Fact]
    public async Task Materialize_UserCancel_TripsLinkedToken_RecordsCancelled()
    {
        var jobId = await SeedMaterializeJob(stagesTotal: 2, stageIndex: 0, feedKey: "binance|BTCUSDT|EqV_1k|");

        _fakeLoad.Behavior = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // block until the linked token trips, then throw OCE(ct)
            return true; // unreachable
        };

        _wakeup.TryEnqueue(jobId);
        var drive = _host.DrainOnceForTest(Ct);

        await _fakeLoad.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        _cancellations.Trip(jobId);
        await drive.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("cancelled", (await _index.GetJob(jobId, Ct))!.State);
        Assert.False(_fakeAgg.Ran); // cancel during stage 0 must not run stage 1
        var cancelEvent = Assert.Single(await _index.GetJobEventsAfter(jobId, 0, Ct), e => e.Kind == "cancelled");
        Assert.Contains("user_cancelled", cancelEvent.PayloadJson);
    }

    // ---- fakes -------------------------------------------------------------

    private sealed class FakeArchiveLoadService : IArchiveLoadService
    {
        public bool Ran { get; private set; }
        public Func<ArchiveLoadRequest, IJobProgressSink, CancellationToken, Task<bool>>? Behavior { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> Run(ArchiveLoadRequest req, IJobProgressSink sink, CancellationToken ct = default)
        {
            Ran = true;
            Started.TrySetResult();
            if (Behavior is not null)
                return await Behavior(req, sink, ct);
            await sink.Complete("""{"ok":true}""", ct); // stage-done (composite terminal owned by worker)
            return true;
        }
    }

    private sealed class FakeAggregationService : IAggregationService
    {
        public bool Ran { get; private set; }
        public Func<AggregationRunRequest, IJobProgressSink, CancellationToken, Task>? Behavior { get; set; }

        public async Task Run(AggregationRunRequest req, IJobProgressSink sink, CancellationToken ct = default)
        {
            Ran = true;
            if (Behavior is not null)
            {
                await Behavior(req, sink, ct);
                return;
            }
            await sink.Complete("""{"ok":true}""", ct);
        }
    }

    // Returns canned stage requests — the worker tests exercise orchestration, not construction.
    private sealed class FakeStageRequestFactory : IMaterializeStageRequestFactory
    {
        private static readonly CollectionAsset _asset = new(
            "binance", "BTC/USDT-PERP", _venue, 2, []);

        public ArchiveLoadRequest BuildLoad(MaterializePlan plan, MaterializeStage.Load stage, string jobId) =>
            new(_asset, "agg-trades", "", new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2), jobId);

        public AggregationRunRequest BuildAggregate(MaterializePlan plan, MaterializeStage.Aggregate stage, string jobId) =>
            new(new AggregationJob(
                JobId: jobId,
                Source: new DataFeedDescriptor("/data", "binance", "BTCUSDT_perp", "ticks", DataFeedKind.Tick),
                AssetDir: "/data/binance/BTCUSDT_perp",
                OutcomeFeedId: "EqV_1k",
                TypeCode: "EqV",
                ThresholdAbsolute: 1000m,
                ThresholdScaled: 1000,
                ThresholdUnit: "base_asset",
                ThresholdInputMode: "convenience",
                ThresholdConvenienceInput: "1k",
                SourceScale: new ScaleContext(0.01m),
                AccumulatorScale: new ScaleContext(0.01m),
                MaxPartitionSizeMB: 1,
                ToolVersion: "test-1.0"));
    }
}
