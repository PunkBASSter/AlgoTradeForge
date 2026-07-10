using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.State;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

/// <summary>
/// Core invariant from spec §5: a fresh rebuild scan produces the same rows as incremental
/// maintenance driven by the matching event stream. Tests four divergence shapes that would
/// cause the two paths to diverge if any handler is inconsistent.
/// </summary>
public sealed class IndexEquivalenceTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atf-equiv-").FullName;
    private string DataRoot => Path.Combine(_root, "data");

    // Incremental side
    private SqliteHistoryIndex _incIndex = null!;
    private IndexWorkProcessor _processor = null!;
    private ISchemaManager _schema = null!;

    // Rebuilt side
    private SqliteHistoryIndex _rebIndex = null!;
    private IndexRebuilder _rebuilder = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var storage = new LocalFileStorage();
        _schema = new FeedSchemaManager(storage);
        var statusStore = new FeedStatusManager(storage);

        // Seed the fixture DataRoot through production writers.
        await SeedFixture(_schema, statusStore);

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = DataRoot });

        // Incremental index DB
        var incInit = new HistoryIndexInitializer(Path.Combine(_root, "inc.sqlite"));
        await incInit.EnsureCreated(Ct);
        _incIndex = new SqliteHistoryIndex(incInit, incInit.ConnectionString + ";Pooling=False");
        _processor = new IndexWorkProcessor(
            _incIndex, new FeedMonthScanner(), _schema, statusStore,
            Substitute.For<IIndexRebuilder>(), options, NullLogger<IndexWorkProcessor>.Instance);

        // Rebuilt index DB
        var rebInit = new HistoryIndexInitializer(Path.Combine(_root, "reb.sqlite"));
        await rebInit.EnsureCreated(Ct);
        _rebIndex = new SqliteHistoryIndex(rebInit, rebInit.ConnectionString + ";Pooling=False");
        _rebuilder = new IndexRebuilder(storage, options, _schema, statusStore,
            new FeedMonthScanner(), _rebIndex, NullLogger<IndexRebuilder>.Instance);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    // -------------------------------------------------------------------------
    // §5 rebuild ≡ incremental invariant
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RebuildAndIncremental_ProduceIdenticalIndex_AcrossAllDivergenceShapes()
    {
        // --- Incremental side ---
        // Shape 1+2+4: BTCUSDT_perp (binance)
        var btcDir = Path.Combine(DataRoot, "binance", "BTCUSDT_perp");
        await _processor.Process(new IndexWork.ManifestTouched(btcDir), Ct);
        // candles — two intervals (Shape 1)
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.Candles, "1h"), Ct);
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.Candles, "4h"), Ct);
        // candle-ext — both candle intervals (Shape 2)
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.CandleExt, "1h"), Ct);
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.CandleExt, "4h"), Ct);
        // ticks — CompleteMonths, no month partitions (Shape 4)
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.Ticks, ""), Ct);
        // funding-rate — always checked by rebuilder; no status file here → no-op
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.FundingRate, ""), Ct);

        // Shape 3: AAPL (NYSE, equity) — no status files; month partitions from CSV
        var aaplDir = Path.Combine(DataRoot, "NYSE", "AAPL");
        await _processor.Process(new IndexWork.ManifestTouched(aaplDir), Ct);
        await _processor.Process(new IndexWork.FeedTouched(aaplDir, FeedNames.Candles, "1d"), Ct);

        // --- Rebuilt side ---
        var jobId = await _rebIndex.CreateJob("rebuild", Ct);
        await _rebuilder.Run(jobId, Ct);

        // --- Snapshot and compare ---
        var incAssets = await SnapshotAssets(_incIndex);
        var rebAssets = await SnapshotAssets(_rebIndex);
        Assert.Equal(rebAssets, incAssets);

        var incStatus = await SnapshotFeedStatus(_incIndex);
        var rebStatus = await SnapshotFeedStatus(_rebIndex);
        Assert.Equal(rebStatus, incStatus);

        var incMonths = await SnapshotMonthPartitions(_incIndex);
        var rebMonths = await SnapshotMonthPartitions(_rebIndex);
        Assert.Equal(rebMonths, incMonths);
    }

    [Fact]
    public async Task FeedRemoval_IncrementalMatchesRebuild()
    {
        var btcDir = Path.Combine(DataRoot, "binance", "BTCUSDT_perp");
        var aaplDir = Path.Combine(DataRoot, "NYSE", "AAPL");

        // Full incremental pass before the removal
        await _processor.Process(new IndexWork.ManifestTouched(btcDir), Ct);
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.Candles, "1h"), Ct);
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.Candles, "4h"), Ct);
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.CandleExt, "1h"), Ct);
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.CandleExt, "4h"), Ct);
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.Ticks, ""), Ct);
        await _processor.Process(new IndexWork.FeedTouched(btcDir, FeedNames.FundingRate, ""), Ct);
        await _processor.Process(new IndexWork.ManifestTouched(aaplDir), Ct);
        await _processor.Process(new IndexWork.FeedTouched(aaplDir, FeedNames.Candles, "1d"), Ct);

        // Remove candle-ext; production fires ManifestChanged → ManifestTouched — drive it directly
        await _schema.RemoveFeed(btcDir, FeedNames.CandleExt, Ct);
        await _processor.Process(new IndexWork.ManifestTouched(btcDir), Ct);

        // Rebuilt side processes the post-removal filesystem
        var jobId = await _rebIndex.CreateJob("rebuild", Ct);
        await _rebuilder.Run(jobId, Ct);

        // Both sides must produce identical snapshots
        Assert.Equal(await SnapshotAssets(_rebIndex), await SnapshotAssets(_incIndex));
        Assert.Equal(await SnapshotFeedStatus(_rebIndex), await SnapshotFeedStatus(_incIndex));
        Assert.Equal(await SnapshotMonthPartitions(_rebIndex), await SnapshotMonthPartitions(_incIndex));
    }

    // -------------------------------------------------------------------------
    // Fixture seeding (four divergence shapes, via production writers where possible)
    // -------------------------------------------------------------------------

    private async Task SeedFixture(ISchemaManager schema, FeedStatusManager statusStore)
    {
        // Shape 1+2+4: BTCUSDT_perp with candles on 1h+4h, candle-ext on both, ticks w/ CompleteMonths
        var btcDir = Path.Combine(DataRoot, "binance", "BTCUSDT_perp");
        Directory.CreateDirectory(Path.Combine(btcDir, FeedNames.Candles));
        Directory.CreateDirectory(Path.Combine(btcDir, FeedNames.CandleExt));
        Directory.CreateDirectory(Path.Combine(btcDir, FeedNames.Ticks));

        // feeds.json: EnsureCandleConfig for 1h then 4h, then EnsureSchema for candle-ext
        await schema.EnsureCandleConfig(btcDir, 2, "1h", Ct);
        await schema.EnsureCandleConfig(btcDir, 2, "4h", Ct);
        await schema.EnsureSchema(btcDir, FeedNames.CandleExt, "1h",
            ["ts", "quote_vol", "trade_count", "taker_buy_vol", "taker_buy_quote_vol"], ct: Ct);

        // candles/1h — two months (Shape 1, two intervals)
        WriteCsv(Path.Combine(btcDir, FeedNames.Candles, "2024-01_1h.csv"), 744);
        WriteCsv(Path.Combine(btcDir, FeedNames.Candles, "2024-02_1h.csv"), 672);
        // candles/4h — one month only (gap: no 2024-02_4h)
        WriteCsv(Path.Combine(btcDir, FeedNames.Candles, "2024-01_4h.csv"), 186);

        // Status files — written via FeedStatusManager (production writer)
        await statusStore.Save(btcDir, FeedNames.Candles, "1h", new FeedStatus
        {
            FeedName = FeedNames.Candles, Interval = "1h",
            FirstTimestamp = 1_704_067_200_000L, LastTimestamp = 1_706_745_600_000L,
            RecordCount = 1416,
            // Shape 1: gap in 1h status
            Gaps = [new DataGap { FromMs = 1_704_499_200_000L, ToMs = 1_704_585_600_000L }],
            Health = CollectionHealth.Degraded,
        }, Ct);
        await statusStore.Save(btcDir, FeedNames.Candles, "4h", new FeedStatus
        {
            FeedName = FeedNames.Candles, Interval = "4h",
            FirstTimestamp = 1_704_067_200_000L, LastTimestamp = 1_706_745_600_000L,
            RecordCount = 186, Health = CollectionHealth.Healthy,
        }, Ct);

        // candle-ext on BOTH candle intervals (Shape 2)
        WriteCsv(Path.Combine(btcDir, FeedNames.CandleExt, "2024-01_1h.csv"), 744);
        WriteCsv(Path.Combine(btcDir, FeedNames.CandleExt, "2024-01_4h.csv"), 186);
        await statusStore.Save(btcDir, FeedNames.CandleExt, "1h", new FeedStatus
        {
            FeedName = FeedNames.CandleExt, Interval = "1h",
            FirstTimestamp = 1_704_067_200_000L, LastTimestamp = 1_706_745_600_000L,
            RecordCount = 744, Health = CollectionHealth.Healthy,
        }, Ct);
        await statusStore.Save(btcDir, FeedNames.CandleExt, "4h", new FeedStatus
        {
            FeedName = FeedNames.CandleExt, Interval = "4h",
            FirstTimestamp = 1_704_067_200_000L, LastTimestamp = 1_706_745_600_000L,
            RecordCount = 186, Health = CollectionHealth.Healthy,
        }, Ct);

        // ticks — CompleteMonths, no CSV partition files (Shape 4)
        await statusStore.Save(btcDir, FeedNames.Ticks, "", new FeedStatus
        {
            FeedName = FeedNames.Ticks, Interval = "",
            RecordCount = 0, Health = CollectionHealth.Healthy,
            CompleteMonths = ["2024-01"],
        }, Ct);

        // Shape 3: AAPL (NYSE equity) — month partitions but NO status files
        var aaplDir = Path.Combine(DataRoot, "NYSE", "AAPL");
        Directory.CreateDirectory(Path.Combine(aaplDir, FeedNames.Candles));
        await schema.EnsureCandleConfig(aaplDir, 2, "1d", Ct);
        WriteCsv(Path.Combine(aaplDir, FeedNames.Candles, "2024-01_1d.csv"), 21);
        // Intentionally no status files — equity-shaped with partitions only
    }

    private static void WriteCsv(string path, int rows)
    {
        var lines = new string[rows + 1];
        lines[0] = "ts,o,h,l,c,v";
        for (var i = 0; i < rows; i++)
            lines[i + 1] = $"{i},1,1,1,1,1";
        File.WriteAllLines(path, lines);
    }

    // -------------------------------------------------------------------------
    // Snapshot helpers — ordered SELECT of all non-volatile columns
    // -------------------------------------------------------------------------

    private static async Task<List<string>> SnapshotAssets(IHistoryIndex index)
    {
        var rows = await index.ListAssets(ct: Ct);
        // Already ordered by (exchange, dir); select non-volatile columns
        return rows.Select(r => $"{r.Exchange}|{r.Dir}|{r.Symbol}|{r.Type}|{r.ManifestJson}").ToList();
    }

    private static async Task<List<string>> SnapshotFeedStatus(IHistoryIndex index)
    {
        // Pull all assets then collect all feed statuses
        var assets = await index.ListAssets(ct: Ct);
        var rows = new List<string>();
        foreach (var a in assets)
        {
            var statuses = await index.GetFeedStatuses(a.Exchange, a.Dir, Ct);
            rows.AddRange(statuses.Select(s =>
                $"{s.Exchange}|{s.Dir}|{s.FeedName}|{s.Interval}|{s.FirstTs}|{s.LastTs}|{s.RecordCount}|{s.Health}|{s.GapsJson}|{s.CompleteMonthsJson}"));
        }
        // GetFeedStatuses is already ordered by (feed_name, interval); rows are by asset order from ListAssets
        return rows;
    }

    private static async Task<List<string>> SnapshotMonthPartitions(IHistoryIndex index)
    {
        var assets = await index.ListAssets(ct: Ct);
        var rows = new List<string>();
        foreach (var a in assets)
        {
            var feedKeys = await index.ListFeedKeys(a.Exchange, a.Dir, Ct);
            foreach (var (feedName, interval) in feedKeys.OrderBy(k => k.FeedName).ThenBy(k => k.Interval))
            {
                if (string.IsNullOrEmpty(interval)) continue;
                var months = await index.GetMonths(a.Exchange, a.Dir, feedName, interval, Ct);
                rows.AddRange(months.Select(m =>
                    $"{a.Exchange}|{a.Dir}|{feedName}|{interval}|{m.Month}|{m.Rows}|{m.FileLen}|{m.FileMtimeUtc}"));
            }
        }
        return rows;
    }
}
