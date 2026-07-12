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

        var sinkFactory = new JobProgressSinkFactory(_index, new JobEventSignal());
        _worker = new LoadJobWorker(
            _wakeup, _index, _fakeArchiveLoad, sinkFactory, new JobCancellationMap(),
            new LoadRequestRehydrator(_plan), NullLogger<LoadJobWorker>.Instance);
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
        var reqJson = LoadRequestRehydrator.Serialize(
            "binance", "BTCUSDT", "CryptoPerpetual", "candles", "1m",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 1));
        var jobId = (await _index.TryAcquireFeedGate(
            "load", "binance|BTCUSDT_perp|candles|1m", "{}", reqJson, Ct) as FeedGateOutcome.Acquired)!.JobId;

        _wakeup.TryEnqueue(jobId);
        await _worker.DrainOnceForTest(Ct); // rehydrate + run (fake IArchiveLoadService returns true)

        Assert.Equal("complete", (await _index.GetJob(jobId, Ct))!.State);
        Assert.Equal("1m", _fakeArchiveLoad.LastRequest!.Interval); // rehydration carried interval + dates
    }

    [Fact]
    public void SeedFromQueued_EnqueuesAllQueuedRows() =>
        Assert.Equal(2, _wakeup.SeedFromQueued(new[] { "a", "b" }));

    private sealed class FakeArchiveLoadService : IArchiveLoadService
    {
        public ArchiveLoadRequest? LastRequest { get; private set; }

        public async Task<bool> Run(ArchiveLoadRequest req, IJobProgressSink sink, CancellationToken ct = default)
        {
            LastRequest = req;
            // Run owns the terminal transition (mirrors the real ArchiveLoadService success path).
            await sink.Complete("""{"ok":true}""", ct);
            return true;
        }
    }
}
