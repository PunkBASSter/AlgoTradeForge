using Microsoft.Data.Sqlite;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Groups;

public sealed class ConvergenceEvaluatorTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-eval-").FullName;
    private SqliteHistoryIndex _index = null!;
    private ConvergenceEvaluator _evaluator = null!;

    // Fixed "now" so tests are deterministic: March 2024
    private static readonly DateOnly NowMonth = new(2024, 3, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated(Ct);
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");
        _evaluator = new ConvergenceEvaluator(_index, new SymbologyRegistry([new BinanceSymbology()]));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    // ---- helpers ----

    // BTC/USDT-PERP on binance → dir = BTCUSDT_perp (from BinanceSymbology)
    private static CollectionGroup MakePerpGroup(
        string name = "g1",
        string historyStart = "2024-01",
        string collect = "eager",
        string[]? intervals = null,
        Dictionary<string, GroupDerived>? derived = null) =>
        new(
            Name: name,
            Enabled: true,
            Exchanges: ["binance"],
            Assets: new GroupAssets(["BTC/USDT-PERP"], historyStart),
            Feeds: new Dictionary<string, GroupFeed>
            {
                ["candles"] = new GroupFeed(collect, intervals ?? ["1h"], "csv"),
            },
            Derived: derived,
            SymbolOverrides: null);

    private static CollectionGroup MakePerpGroupWithFeed(
        string feedName,
        string collect = "eager",
        string historyStart = "2024-01") =>
        new(
            Name: "g1",
            Enabled: true,
            Exchanges: ["binance"],
            Assets: new GroupAssets(["BTC/USDT-PERP"], historyStart),
            Feeds: new Dictionary<string, GroupFeed>
            {
                [feedName] = new GroupFeed(collect, null, "csv"),
            },
            Derived: null,
            SymbolOverrides: null);

    private async Task SeedMonths(string exchange, string dir, string feedName, string interval,
        params string[] months)
    {
        await _index.UpsertAsset(new AssetIndexRow(exchange, dir, "BTCUSDT", "CryptoPerpetual", "{}"), Ct);
        var rows = months.Select(m => new MonthPartitionRow(m, 100, 1000, "mt")).ToList();
        await _index.ReplaceMonths(exchange, dir, feedName, interval, rows, Ct);
    }

    private async Task SeedCompleteMonths(string exchange, string dir, string feedName, params string[] months)
    {
        await _index.UpsertAsset(new AssetIndexRow(exchange, dir, "BTCUSDT", "CryptoPerpetual", "{}"), Ct);
        var cm = System.Text.Json.JsonSerializer.Serialize(months);
        await _index.UpsertFeedStatus(
            new FeedStatusIndexRow(exchange, dir, feedName, "", null, null, 0, "Healthy", "[]", cm), Ct);
    }

    // ---- tuple-status tests ----

    [Fact]
    public async Task Candles_ThreeOf_Three_Months_IsMaterialized()
    {
        await SeedMonths("binance", "BTCUSDT_perp", "candles", "1h",
            "2024-01", "2024-02", "2024-03");

        var report = await _evaluator.Evaluate([MakePerpGroup()], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("materialized", ts.Status);
        Assert.Equal(3, ts.MonthsExpected);
        Assert.Equal(3, ts.MonthsCovered);
    }

    [Fact]
    public async Task Candles_OneOf_Three_Months_IsPartial()
    {
        await SeedMonths("binance", "BTCUSDT_perp", "candles", "1h", "2024-01");

        var report = await _evaluator.Evaluate([MakePerpGroup()], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("partial", ts.Status);
        Assert.Equal(3, ts.MonthsExpected);
        Assert.Equal(1, ts.MonthsCovered);
    }

    [Fact]
    public async Task Eager_NoCoverage_IsMissing()
    {
        // no index rows — 0 covered, expected = 3
        var report = await _evaluator.Evaluate([MakePerpGroup(collect: "eager")], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("missing", ts.Status);
        Assert.Equal(3, ts.MonthsExpected);
        Assert.Equal(0, ts.MonthsCovered);
    }

    [Fact]
    public async Task OnDemand_NoCoverage_IsOnDemand_NotMissing()
    {
        // on-demand + 0 covered → on-demand (expected state)
        var report = await _evaluator.Evaluate([MakePerpGroup(collect: "on-demand")], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("on-demand", ts.Status);
    }

    [Fact]
    public async Task OnDemand_WithRows_FollowsNormalRules_Partial()
    {
        // on-demand + rows → skip on-demand rule → partial (1 covered, 3 expected)
        await SeedMonths("binance", "BTCUSDT_perp", "candles", "1h", "2024-01");

        var report = await _evaluator.Evaluate([MakePerpGroup(collect: "on-demand")], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("partial", ts.Status);
        Assert.Equal(1, ts.MonthsCovered);
    }

    [Fact]
    public async Task OnDemand_WithThreeRows_FollowsNormalRules_Materialized()
    {
        // on-demand + full coverage → materialized
        await SeedMonths("binance", "BTCUSDT_perp", "candles", "1h",
            "2024-01", "2024-02", "2024-03");

        var report = await _evaluator.Evaluate([MakePerpGroup(collect: "on-demand")], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("materialized", ts.Status);
    }

    [Fact]
    public async Task Derived_EagerMaterialize_NoCoverage_IsOnDemand_ViaisDerived()
    {
        // IsDerived = true, materialize = eager, 0 covered → on-demand (phase 2 cannot materialize derived)
        var derived = new Dictionary<string, GroupDerived>
        {
            ["EqV_1m_1k"] = new GroupDerived("candles", "EqV", "1000", "1m", "eager"),
        };
        var group = new CollectionGroup(
            Name: "g1", Enabled: true,
            Exchanges: ["binance"],
            Assets: new GroupAssets(["BTC/USDT-PERP"], "2024-01"),
            Feeds: new Dictionary<string, GroupFeed>
            {
                ["candles"] = new GroupFeed("eager", ["1m"], "csv"),
            },
            Derived: derived,
            SymbolOverrides: null);

        // no index rows for the derived feed
        var report = await _evaluator.Evaluate([group], NowMonth, Ct);

        var derived_ts = report.Tuples.Single(t => t.Tuple.IsDerived);
        Assert.Equal("on-demand", derived_ts.Status);
        Assert.Equal(0, derived_ts.MonthsCovered);
    }

    [Fact]
    public async Task FutureHistoryStart_Expected_Zero_IsMaterialized()
    {
        // historyStart is 3 months in the future → expected = 0 → vacuously materialized
        var report = await _evaluator.Evaluate(
            [MakePerpGroup(historyStart: "2024-06")], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("materialized", ts.Status);
        Assert.Equal(0, ts.MonthsExpected);
        Assert.Equal(0, ts.MonthsCovered);
    }

    [Fact]
    public async Task UnsupportedFut_ProducesNoTupleStatuses()
    {
        // BTC/USDT-FUT-2025-06 → BinanceSymbology rejects → state.Unsupported → NOT in Tuples
        var group = new CollectionGroup(
            Name: "g1", Enabled: true,
            Exchanges: ["binance"],
            Assets: new GroupAssets(["BTC/USDT-FUT-2025-06"], "2024-01"),
            Feeds: new Dictionary<string, GroupFeed>
            {
                ["candles"] = new GroupFeed("eager", ["1h"], "csv"),
            },
            Derived: null,
            SymbolOverrides: null);

        var report = await _evaluator.Evaluate([group], NowMonth, Ct);

        Assert.Empty(report.Tuples);
        Assert.Empty(report.Conflicts);
    }

    [Fact]
    public async Task IntervalLess_CompleteMonths_CountedAsCovered()
    {
        // funding-rate (interval "") with CompleteMonths ["2024-01","2024-02","2024-03"] → materialized
        await SeedCompleteMonths("binance", "BTCUSDT_perp", "funding-rate",
            "2024-01", "2024-02", "2024-03");

        var report = await _evaluator.Evaluate(
            [MakePerpGroupWithFeed("funding-rate", collect: "eager")], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("materialized", ts.Status);
        Assert.Equal(3, ts.MonthsCovered);
    }

    [Fact]
    public async Task Conflicts_PassedThrough_InReport()
    {
        // two groups with conflicting formats for same feed → GroupConflict → ConvergenceReport.Conflicts
        var g1 = MakePerpGroup("g1");
        var g2 = new CollectionGroup(
            Name: "g2", Enabled: true,
            Exchanges: ["binance"],
            Assets: new GroupAssets(["BTC/USDT-PERP"], "2024-01"),
            Feeds: new Dictionary<string, GroupFeed>
            {
                ["candles"] = new GroupFeed("eager", ["1h"], "parquet"),
            },
            Derived: null,
            SymbolOverrides: null);

        var report = await _evaluator.Evaluate([g1, g2], NowMonth, Ct);

        Assert.Single(report.Conflicts);
        Assert.Equal("format", report.Conflicts[0].Kind);
    }

    // ---- orphan detection tests ----

    [Fact]
    public async Task EquityShapedRows_WithNoClaimingTuple_AreOrphaned()
    {
        // equity-shaped row: exchange=nasdaq, dir=NFLX, feed=candles, interval=1d
        await _index.UpsertAsset(new AssetIndexRow("nasdaq", "NFLX", "NFLX", "Equity", "{}"), Ct);
        await _index.ReplaceMonths("nasdaq", "NFLX", "candles", "1d",
            [new MonthPartitionRow("2024-01", 21, 1, "mt")], Ct);

        // groups only declare binance BTC/USDT-PERP; equity is unclaimed → orphan
        await SeedMonths("binance", "BTCUSDT_perp", "candles", "1h",
            "2024-01", "2024-02", "2024-03");

        var report = await _evaluator.Evaluate([MakePerpGroup()], NowMonth, Ct);

        var orphan = Assert.Single(report.Orphaned);
        Assert.Equal("nasdaq", orphan.Exchange);
        Assert.Equal("NFLX", orphan.Dir);
        Assert.Equal("candles", orphan.FeedName);
        Assert.Equal("1d", orphan.Interval);
    }

    [Fact]
    public async Task MixedCaseIndexExchange_MatchedByLowercaseTuple_NotOrphaned()
    {
        // index has exchange="BINANCE" (uppercase); tuple has exchange="binance" (lowercase)
        // Orphan detection uses OrdinalIgnoreCase → BINANCE matched by binance → NOT an orphan.
        // (Status query uses lowercase and finds 0 rows — real collection always stores lowercase,
        // so this case is purely a guard for manually-seeded or migrated data.)
        await _index.UpsertAsset(new AssetIndexRow("BINANCE", "BTCUSDT_perp", "BTCUSDT", "CryptoPerpetual", "{}"), Ct);
        await _index.ReplaceMonths("BINANCE", "BTCUSDT_perp", "candles", "1h",
            [new MonthPartitionRow("2024-01", 100, 1, "mt"),
             new MonthPartitionRow("2024-02", 100, 1, "mt"),
             new MonthPartitionRow("2024-03", 100, 1, "mt")], Ct);

        var report = await _evaluator.Evaluate([MakePerpGroup()], NowMonth, Ct);

        // Primary assertion: the index row is claimed by the tuple → NOT in orphans
        Assert.DoesNotContain(report.Orphaned,
            o => string.Equals(o.Exchange, "BINANCE", StringComparison.OrdinalIgnoreCase)
                 && o.Dir == "BTCUSDT_perp" && o.FeedName == "candles");
    }

    [Fact]
    public async Task OrphanedFeedOnClaimedAsset_IsOrphaned()
    {
        // asset has two feeds; only one is claimed by a tuple
        await SeedMonths("binance", "BTCUSDT_perp", "candles", "1h",
            "2024-01", "2024-02", "2024-03");
        // extra feed not in any group
        await _index.ReplaceMonths("binance", "BTCUSDT_perp", "mark-price", "",
            [new MonthPartitionRow("2024-01", 10, 1, "mt")], Ct);

        var report = await _evaluator.Evaluate([MakePerpGroup()], NowMonth, Ct);

        var orphan = Assert.Single(report.Orphaned);
        Assert.Equal("mark-price", orphan.FeedName);
        Assert.Equal("", orphan.Interval);
    }
}
