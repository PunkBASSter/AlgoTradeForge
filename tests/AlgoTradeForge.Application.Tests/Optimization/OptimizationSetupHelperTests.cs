using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Application.Tests.TestUtilities;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

/// <summary>
/// Phase 4 (P4-12) coverage for the polymorphic optimization path.
/// Pins kind-aware cache keying so AltBar feeds at the same nominal source don't alias,
/// and pins the dual-key trial carrier so AltBar FeedIds round-trip through run records.
/// </summary>
public sealed class OptimizationSetupHelperTests
{
    private readonly IAssetRepository _assetRepository = Substitute.For<IAssetRepository>();
    private readonly IHistoryRepository _historyRepository = Substitute.For<IHistoryRepository>();

    private OptimizationSetupHelper CreateHelper()
    {
        var engine = new BacktestEngine(
            Substitute.For<IBarMatcher>(), new OrderValidator());
        return new OptimizationSetupHelper(
            engine, _assetRepository, _historyRepository,
            Substitute.For<IMetricsCalculator>(),
            Substitute.For<IOptimizationSpaceProvider>(),
            Substitute.For<IRunRepository>(),
            NullLogger<OptimizationSetupHelper>.Instance);
    }

    [Fact]
    public async Task ResolveAndCacheAsync_TwoAltBarsAtSameSource_DifferentFeedIds_CacheKeysDistinct()
    {
        // Regression: BacktestInputsFormatter.Key encodes asset:exchange:feed:role per TRD §9.3.
        // Two EqV alt-bars built from the same 1m source but with different thresholds (1000 vs
        // 5000 base units) MUST hash distinctly — the prior CacheKey(asset, TimeSpan) used the
        // nominal TimeFrame and would have aliased these two distinct feeds.
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));
        _historyRepository.Load(Arg.Any<Asset>(), Arg.Any<DataFeedSubscription>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // Return distinct series per FeedId so cache aliasing would surface as wrong data.
                var sub = (AltBarSubscription)call.Arg<DataFeedSubscription>();
                var bars = sub.FeedId == "EqV_1m_1000" ? 100 : 500;
                return TestBars.CreateSeries(bars);
            });

        var helper = CreateHelper();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
        var resolvedSubs = new List<DataFeedSubscription>();

        var sub1 = new AltBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, "EqV_1m_1000");
        var sub2 = new AltBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, "EqV_1m_5000");

        await helper.ResolveAndCacheAsync(sub1, resolvedSubs, dataCache,
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), TestContext.Current.CancellationToken);
        await helper.ResolveAndCacheAsync(sub2, resolvedSubs, dataCache,
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30), TestContext.Current.CancellationToken);

        Assert.Equal(2, dataCache.Count);  // distinct keys, no aliasing
        Assert.Contains("EqV_1m_1000", string.Join("|", dataCache.Keys));
        Assert.Contains("EqV_1m_5000", string.Join("|", dataCache.Keys));

        // Confirm the cached series sizes match the loader's distinct return values
        var key1Hits = dataCache.First(kv => kv.Key.Contains("EqV_1m_1000"));
        var key2Hits = dataCache.First(kv => kv.Key.Contains("EqV_1m_5000"));
        Assert.Equal(100, key1Hits.Value.Series.Count);
        Assert.Equal(500, key2Hits.Value.Series.Count);
    }

    [Fact]
    public async Task ResolveAndCacheAsync_AltBarPrimary_SynthesizesSourceTimeFrameOnStrategySide()
    {
        // P4-12: strategy-side DataFeedSubscription gets a placeholder TimeFrame derived from the
        // alt-bar source code. EqV_1m_500m → source = "1m" → TimeFrame.Parse("1m").
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));
        _historyRepository.Load(Arg.Any<Asset>(), Arg.Any<DataFeedSubscription>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestBars.CreateSeries(10));

        var helper = CreateHelper();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
        var resolvedSubs = new List<DataFeedSubscription>();

        await helper.ResolveAndCacheAsync(
            new AltBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, "EqV_1m_500m"),
            resolvedSubs, dataCache,
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30),
            TestContext.Current.CancellationToken);

        var resolved = Assert.Single(resolvedSubs);
        Assert.IsType<AltBarSubscription>(resolved);
        Assert.Equal("EqV_1m_500m", resolved.FeedKey());
    }

    [Fact]
    public async Task ResolveAndCacheAsync_TickPrimary_UsesTickSentinelTimeFrame()
    {
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));
        _historyRepository.Load(Arg.Any<Asset>(), Arg.Any<DataFeedSubscription>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestBars.CreateSeries(10));

        var helper = CreateHelper();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
        var resolvedSubs = new List<DataFeedSubscription>();

        await helper.ResolveAndCacheAsync(
            new TickSubscription("BTCUSDT", "Binance", DataFeedRole.Primary),
            resolvedSubs, dataCache,
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30),
            TestContext.Current.CancellationToken);

        var resolved = Assert.Single(resolvedSubs);
        Assert.IsType<TickSubscription>(resolved);
        Assert.Equal("ticks", resolved.FeedKey());
    }

    // -------- Phase 4 (P4-14, TRD §9.6): ExpandMultiPrimary --------

    [Fact]
    public void ExpandMultiPrimary_NullInput_ReturnsEmpty()
    {
        var result = OptimizationSetupHelper.ExpandMultiPrimary(null);
        Assert.Empty(result);
    }

    [Fact]
    public void ExpandMultiPrimary_EmptyInput_ReturnsEmpty()
    {
        var result = OptimizationSetupHelper.ExpandMultiPrimary(new List<List<DataFeedSubscription>>());
        Assert.Empty(result);
    }

    [Fact]
    public void ExpandMultiPrimary_SinglePrimaryDss_PassesThroughIdentity()
    {
        var btc = new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        List<List<DataFeedSubscription>> input = [[btc]];

        var result = OptimizationSetupHelper.ExpandMultiPrimary(input);

        var dss = Assert.Single(result);
        Assert.Same(btc, dss[0]);
    }

    [Fact]
    public void ExpandMultiPrimary_MultiPrimaryWithSharedSide_ExpandsToOnePerPrimary()
    {
        // [Primary(A), Primary(B), Side(X)] → [[A,X],[B,X]]
        var primaryA = new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var primaryB = new AltBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, "EqV_1m_1000");
        var sideX = new SideFeedSubscription("BTCUSDT", "Binance", DataFeedRole.Side, "funding-rate");
        List<List<DataFeedSubscription>> input = [[primaryA, primaryB, sideX]];

        var result = OptimizationSetupHelper.ExpandMultiPrimary(input);

        Assert.Equal(2, result.Count);
        // First expanded DSS: [primaryA, sideX]
        Assert.Equal(2, result[0].Count);
        Assert.Same(primaryA, result[0][0]);
        Assert.Same(sideX, result[0][1]);
        // Second expanded DSS: [primaryB, sideX]
        Assert.Equal(2, result[1].Count);
        Assert.Same(primaryB, result[1][0]);
        Assert.Same(sideX, result[1][1]);
    }

    [Fact]
    public void ExpandMultiPrimary_MixedCardinalities_ExpandsCorrectly()
    {
        // DSS 0: 3 primaries, 1 side → 3 expanded DSSes
        // DSS 1: 1 primary, 0 sides   → 1 expanded DSS (identity)
        // Total: 4 expanded DSSes in input order
        var pA = new TimeBarSubscription("BTC", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var pB = new TimeBarSubscription("ETH", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var pC = new TimeBarSubscription("SOL", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var sX = new SideFeedSubscription("BTC", "Binance", DataFeedRole.Side, "funding-rate");
        var pD = new TimeBarSubscription("XRP", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));

        List<List<DataFeedSubscription>> input =
        [
            [pA, pB, pC, sX],
            [pD],
        ];

        var result = OptimizationSetupHelper.ExpandMultiPrimary(input);

        Assert.Equal(4, result.Count);
        Assert.Same(pA, result[0][0]); Assert.Same(sX, result[0][1]);
        Assert.Same(pB, result[1][0]); Assert.Same(sX, result[1][1]);
        Assert.Same(pC, result[2][0]); Assert.Same(sX, result[2][1]);
        Assert.Same(pD, result[3][0]);
        Assert.Single(result[3]); // pD has no sides
    }

    [Fact]
    public void ExpandMultiPrimary_DssWithNoPrimaries_ThrowsArgumentException()
    {
        // Side-only DSS is invalid — every DSS must drive its own bar clock.
        var sX = new SideFeedSubscription("BTC", "Binance", DataFeedRole.Side, "funding-rate");
        List<List<DataFeedSubscription>> input = [[sX]];

        var ex = Assert.Throws<ArgumentException>(() =>
            OptimizationSetupHelper.ExpandMultiPrimary(input));
        Assert.Contains("Role=Primary", ex.Message);
        Assert.Contains("[0]", ex.Message);
    }

    [Fact]
    public void ExpandMultiPrimary_PrimariesWithEqualValues_StillProducesDistinctDsses()
    {
        // No input-side dedup — duplicate primaries each get their own DSS.
        // Per-primary normalizer dedup is the param-grid's job, not the expansion's.
        var p1 = new TimeBarSubscription("BTC", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var p2 = new TimeBarSubscription("BTC", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        List<List<DataFeedSubscription>> input = [[p1, p2]];

        var result = OptimizationSetupHelper.ExpandMultiPrimary(input);

        Assert.Equal(2, result.Count);
        Assert.Same(p1, result[0][0]);
        Assert.Same(p2, result[1][0]);
    }

    [Fact]
    public void ExpandMultiPrimary_SideOrderPreserved()
    {
        // Side feeds keep their original order (after the primary).
        var pA = new TimeBarSubscription("BTC", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var s1 = new SideFeedSubscription("BTC", "Binance", DataFeedRole.Side, "funding-rate");
        var s2 = new SideFeedSubscription("BTC", "Binance", DataFeedRole.Side, "open-interest");
        var s3 = new SideFeedSubscription("BTC", "Binance", DataFeedRole.Side, "long-short-ratio");
        List<List<DataFeedSubscription>> input = [[pA, s1, s2, s3]];

        var result = OptimizationSetupHelper.ExpandMultiPrimary(input);

        var dss = Assert.Single(result);
        Assert.Equal(4, dss.Count);
        Assert.Same(pA, dss[0]);
        Assert.Same(s1, dss[1]);
        Assert.Same(s2, dss[2]);
        Assert.Same(s3, dss[3]);
    }

    [Fact]
    public void ExpandMultiPrimary_InterleavedPrimariesAndSides_CollapseToCanonicalOrder()
    {
        // Pins the canonical post-expansion ordering: each output DSS is
        // [primary_i, ...all_sides_in_input_order], regardless of how primaries and
        // sides were interleaved in the input. Downstream code (cache keys, persistence,
        // engine glob resolution) relies on primary-at-index-0; if a future change ever
        // tries to "preserve" interleaving, this test will catch the regression.
        var pA = new TimeBarSubscription("BTC", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var sX = new SideFeedSubscription("BTC", "Binance", DataFeedRole.Side, "funding-rate");
        var pB = new TimeBarSubscription("ETH", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var sY = new SideFeedSubscription("BTC", "Binance", DataFeedRole.Side, "open-interest");

        // Input ordering: [Primary, Side, Primary, Side] (interleaved).
        List<List<DataFeedSubscription>> input = [[pA, sX, pB, sY]];

        var result = OptimizationSetupHelper.ExpandMultiPrimary(input);

        Assert.Equal(2, result.Count);
        // Each child DSS: [primary, side1, side2] — primary first, sides in input order.
        Assert.Same(pA, result[0][0]); Assert.Same(sX, result[0][1]); Assert.Same(sY, result[0][2]);
        Assert.Same(pB, result[1][0]); Assert.Same(sX, result[1][1]); Assert.Same(sY, result[1][2]);
    }
}
