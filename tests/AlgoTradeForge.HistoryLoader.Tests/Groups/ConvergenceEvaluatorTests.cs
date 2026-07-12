using Microsoft.Data.Sqlite;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using NSubstitute;
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
        // mark-price stored at its real cadence interval "1h" (prod-shaped), not declared
        await _index.ReplaceMonths("binance", "BTCUSDT_perp", "mark-price", "1h",
            [new MonthPartitionRow("2024-01", 10, 1, "mt")], Ct);

        var report = await _evaluator.Evaluate([MakePerpGroup()], NowMonth, Ct);

        var orphan = Assert.Single(report.Orphaned);
        Assert.Equal("mark-price", orphan.FeedName);
        Assert.Equal("1h", orphan.Interval);
    }

    // ---- F1: non-candles coverage across cadence intervals ----

    [Fact]
    public async Task NonCandles_IndexRowsAtCadenceInterval_IsMaterialized()
    {
        // mark-price declared (tuple interval ""), index rows at "1h" — fully covered
        await SeedMonths("binance", "BTCUSDT_perp", "mark-price", "1h",
            "2024-01", "2024-02", "2024-03");

        var report = await _evaluator.Evaluate(
            [MakePerpGroupWithFeed("mark-price", collect: "eager")], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("materialized", ts.Status);
        Assert.Equal(3, ts.MonthsCovered);
    }

    [Fact]
    public async Task NonCandles_PartialCadenceIntervalCoverage_IsPartial()
    {
        // mark-price declared, only 2 of 3 expected months present
        await SeedMonths("binance", "BTCUSDT_perp", "mark-price", "1h",
            "2024-01", "2024-02");

        var report = await _evaluator.Evaluate(
            [MakePerpGroupWithFeed("mark-price", collect: "eager")], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("partial", ts.Status);
        Assert.Equal(2, ts.MonthsCovered);
    }

    [Fact]
    public async Task DeclaredOI_WithRowsAt5m_NotMissing()
    {
        // open-interest stored at "5m" cadence; declared in group (tuple interval "")
        await SeedMonths("binance", "BTCUSDT_perp", "open-interest", "5m",
            "2024-01", "2024-02", "2024-03");

        var report = await _evaluator.Evaluate(
            [MakePerpGroupWithFeed("open-interest", collect: "eager")], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.NotEqual("missing", ts.Status);
        Assert.Equal(3, ts.MonthsCovered);
    }

    // ---- F2: candle-ext claimed by candles tuple ----

    [Fact]
    public async Task DeclaredCandles_SideOutputCandleExt_NotOrphaned()
    {
        // Group declares candles [1h]; candle-ext is co-written at the same interval by the collector.
        // candle-ext rows must NOT appear in orphans because the candles tuple claims them.
        await SeedMonths("binance", "BTCUSDT_perp", "candles", "1h",
            "2024-01", "2024-02", "2024-03");
        await _index.ReplaceMonths("binance", "BTCUSDT_perp", "candle-ext", "1h",
            [new MonthPartitionRow("2024-01", 100, 1, "mt"),
             new MonthPartitionRow("2024-02", 100, 1, "mt"),
             new MonthPartitionRow("2024-03", 100, 1, "mt")], Ct);

        var report = await _evaluator.Evaluate([MakePerpGroup()], NowMonth, Ct);

        Assert.DoesNotContain(report.Orphaned,
            o => o.FeedName == "candle-ext" && o.Dir == "BTCUSDT_perp");
    }

    [Fact]
    public async Task UndeclaredAsset_CandleExtRows_AreOrphaned()
    {
        // An asset with candle-ext rows but no declaring group → orphaned
        await _index.UpsertAsset(
            new AssetIndexRow("binance", "ETHUSDT_perp", "ETHUSDT", "CryptoPerpetual", "{}"), Ct);
        await _index.ReplaceMonths("binance", "ETHUSDT_perp", "candle-ext", "1h",
            [new MonthPartitionRow("2024-01", 100, 1, "mt")], Ct);

        // Groups only declare BTC/USDT-PERP
        await SeedMonths("binance", "BTCUSDT_perp", "candles", "1h", "2024-01", "2024-02", "2024-03");

        var report = await _evaluator.Evaluate([MakePerpGroup()], NowMonth, Ct);

        Assert.Contains(report.Orphaned,
            o => o.FeedName == "candle-ext" && o.Dir == "ETHUSDT_perp");
    }

    // ---- Task 7: discovery clamp, blocked, awaiting-data, one-SQL orphans ----

    private static string[] MonthsRange(string startYm, DateOnly endMonth)
    {
        var cur = DateOnly.ParseExact(startYm + "-01", "yyyy-MM-dd");
        var end = new DateOnly(endMonth.Year, endMonth.Month, 1);
        var list = new List<string>();
        while (cur <= end) { list.Add(cur.ToString("yyyy-MM")); cur = cur.AddMonths(1); }
        return list.ToArray();
    }

    [Fact]
    public async Task Expected_ClampsToDiscoveredFirstMonth()
    {
        // historyStart 2020-01, discovery 2023-05, covered = every month 2023-05..now (2024-03).
        // Without the clamp expected = Jan2020..Mar2024 (51) → falsely "partial". With the clamp
        // expected = May2023..Mar2024 (11) == covered → materialized (the idempotency case).
        var covered = MonthsRange("2023-05", NowMonth);
        await SeedMonths("binance", "BTCUSDT_perp", "candles", "1h", covered);
        await _index.SetDiscoveredFirstMonth("binance", "BTCUSDT_perp", "candles", "1h", "2023-05", Ct);

        var report = await _evaluator.Evaluate([MakePerpGroup(historyStart: "2020-01")], NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("materialized", ts.Status);
        Assert.Equal(11, ts.MonthsExpected);
        Assert.Equal(ts.MonthsExpected, ts.MonthsCovered);
    }

    [Fact]
    public async Task Blocked_WinsOverMissing()
    {
        // eager candles, 0 covered → normally "missing"; the asset is in the blocked set → "blocked".
        var state = GroupExpansion.Expand(
            [MakePerpGroup()], new SymbologyRegistry([new BinanceSymbology()]));
        var blocked = new List<BlockedAsset>
        {
            new("binance", "BTC/USDT-PERP", "BTCUSDT_perp", "no instrument scale"),
        };

        var report = await _evaluator.Evaluate(state, blocked, NowMonth, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("blocked", ts.Status);
    }

    [Fact]
    public async Task StreamFeed_ZeroObserved_IsAwaitingData_NotMaterialized()
    {
        // liquidations, no rows: awaiting-data for both a past and a future historyStart
        // (future ⇒ expected 0, but awaiting-data fires before the missing/materialized rules).
        var past = await _evaluator.Evaluate(
            [MakePerpGroupWithFeed("liquidations", collect: "eager", historyStart: "2024-01")], NowMonth, Ct);
        Assert.Equal("awaiting-data", Assert.Single(past.Tuples).Status);

        var future = await _evaluator.Evaluate(
            [MakePerpGroupWithFeed("liquidations", collect: "eager", historyStart: "2024-06")], NowMonth, Ct);
        Assert.Equal("awaiting-data", Assert.Single(future.Tuples).Status);
    }

    [Fact]
    public async Task StreamFeed_WithRows_ExpectedFromFirstObserved()
    {
        // now = 2026-07, liquidations observed 2026-05..2026-07, historyStart 2024-01.
        // expected counts from first observed (2026-05), not historyStart → 3 == covered → materialized.
        var now = new DateOnly(2026, 7, 1);
        await SeedCompleteMonths("binance", "BTCUSDT_perp", "liquidations",
            "2026-05", "2026-06", "2026-07");

        var report = await _evaluator.Evaluate(
            [MakePerpGroupWithFeed("liquidations", collect: "eager", historyStart: "2024-01")], now, Ct);

        var ts = Assert.Single(report.Tuples);
        Assert.Equal("materialized", ts.Status);
        Assert.Equal(3, ts.MonthsCovered);
        Assert.Equal(3, ts.MonthsExpected);
    }

    [Fact]
    public async Task OrphanScan_SingleQuery_MatchesOldSemantics()
    {
        // Same shape as OrphanedFeedOnClaimedAsset (candles 1h claimed, mark-price 1h orphan) but
        // over a substitute index: assert exactly one ListAllFeedKeys and zero per-asset ListFeedKeys.
        var index = Substitute.For<IHistoryIndex>();
        index.ListDiscoveredFirstMonths(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredFirstMonthRow>>([]));
        index.GetFeedStatuses(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<FeedStatusIndexRow>>([]));
        index.GetMonths("binance", "BTCUSDT_perp", "candles", "1h", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MonthPartitionRow>>(
            [
                new("2024-01", 100, 1, "mt"),
                new("2024-02", 100, 1, "mt"),
                new("2024-03", 100, 1, "mt"),
            ]));
        index.ListAllFeedKeys(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(string, string, string, string)>>(
            [
                ("binance", "BTCUSDT_perp", "candles", "1h"),
                ("binance", "BTCUSDT_perp", "mark-price", "1h"),
            ]));

        var evaluator = new ConvergenceEvaluator(index, new SymbologyRegistry([new BinanceSymbology()]));

        var report = await evaluator.Evaluate([MakePerpGroup()], NowMonth, Ct);

        var orphan = Assert.Single(report.Orphaned);
        Assert.Equal("mark-price", orphan.FeedName);
        Assert.Equal("1h", orphan.Interval);
        await index.Received(1).ListAllFeedKeys(Arg.Any<CancellationToken>());
        await index.DidNotReceive().ListFeedKeys(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
