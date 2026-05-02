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
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(call =>
            {
                // Return distinct series per FeedId so cache aliasing would surface as wrong data.
                var sub = (AltBarSubscription)call.Arg<DataFeedSubscription>();
                var bars = sub.FeedId == "EqV_1m_1000" ? 100 : 500;
                return TestBars.CreateSeries(bars);
            });

        var helper = CreateHelper();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
        var resolvedSubs = new List<DataSubscription>();

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
        // P4-12: strategy-side DataSubscription gets a placeholder TimeFrame derived from the
        // alt-bar source code. EqV_1m_500m → source = "1m" → TimeFrame.Parse("1m").
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));
        _historyRepository.Load(Arg.Any<Asset>(), Arg.Any<DataFeedSubscription>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(TestBars.CreateSeries(10));

        var helper = CreateHelper();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
        var resolvedSubs = new List<DataSubscription>();

        await helper.ResolveAndCacheAsync(
            new AltBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, "EqV_1m_500m"),
            resolvedSubs, dataCache,
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30),
            TestContext.Current.CancellationToken);

        var resolved = Assert.Single(resolvedSubs);
        Assert.Equal("1m", resolved.TimeFrame.Code);
        Assert.Equal("EqV_1m_500m", resolved.FeedKey);
    }

    [Fact]
    public async Task ResolveAndCacheAsync_TickPrimary_UsesTickSentinelTimeFrame()
    {
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));
        _historyRepository.Load(Arg.Any<Asset>(), Arg.Any<DataFeedSubscription>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(TestBars.CreateSeries(10));

        var helper = CreateHelper();
        var dataCache = new Dictionary<string, (Asset Asset, TimeSeries<Int64Bar> Series)>();
        var resolvedSubs = new List<DataSubscription>();

        await helper.ResolveAndCacheAsync(
            new TickSubscription("BTCUSDT", "Binance", DataFeedRole.Primary),
            resolvedSubs, dataCache,
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30),
            TestContext.Current.CancellationToken);

        var resolved = Assert.Single(resolvedSubs);
        Assert.Equal("1m", resolved.TimeFrame.Code);  // sentinel
        Assert.Equal("ticks", resolved.FeedKey);
    }
}
