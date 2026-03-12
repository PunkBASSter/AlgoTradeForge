using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class BacktestEngineFeedTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly long StartMs = Start.ToUnixTimeMilliseconds();
    private static readonly long StepMs = (long)OneMinute.TotalMilliseconds;

    private static readonly CryptoPerpetualAsset PerpAsset = CryptoPerpetualAsset.Create("BTCUSDT_PERP", "Binance",
        decimalDigits: 2);

    private readonly IBarMatcher _barMatcher;
    private readonly BacktestEngine _engine;

    public BacktestEngineFeedTests()
    {
        _barMatcher = Substitute.For<IBarMatcher>();
        _engine = new BacktestEngine(_barMatcher, new OrderValidator());
    }

    private static BacktestOptions CreateOptions() =>
        new()
        {
            InitialCash = 1_000_000L,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

    #region IFeedContext wiring

    [Fact]
    public void Run_WithFeedContext_WiresIFeedContextToStrategy()
    {
        var bars = TestBars.CreateSeries(Start, OneMinute, 1, startPrice: 50000);
        var strategy = new FeedAwareTestStrategy();
        strategy.DataSubscriptions.Add(new DataSubscription(PerpAsset, OneMinute));

        var feedContext = new BacktestFeedContext();
        feedContext.Register("funding",
            new DataFeedSchema("funding", ["fundingRate"]),
            new FeedSeries([StartMs], [[0.0001]]));

        _engine.Run([bars], strategy, CreateOptions(), feedContext: feedContext);

        Assert.NotNull(strategy.Feeds);
        Assert.IsType<BacktestFeedContext>(strategy.Feeds);
    }

    [Fact]
    public void Run_NoFeedContext_WiresNullFeedContext()
    {
        var bars = TestBars.CreateSeries(Start, OneMinute, 1, startPrice: 50000);
        var strategy = new FeedAwareTestStrategy();
        strategy.DataSubscriptions.Add(new DataSubscription(PerpAsset, OneMinute));

        _engine.Run([bars], strategy, CreateOptions());

        Assert.NotNull(strategy.Feeds);
        Assert.IsType<NullFeedContext>(strategy.Feeds);
    }

    #endregion

    #region Feed data available during OnBarComplete

    [Fact]
    public void Run_StrategyCanQueryFeedDuringOnBarComplete()
    {
        // 2 bars: t=0, t=60s
        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 50000);

        // Funding at t=60s (arrives at bar 1)
        var feedContext = new BacktestFeedContext();
        feedContext.Register("funding",
            new DataFeedSchema("funding", ["fundingRate", "markPrice"]),
            new FeedSeries([StartMs + StepMs], [[0.0001], [50100.0]]));

        double? capturedFundingRate = null;
        var strategy = new FeedAwareTestStrategy();
        strategy.DataSubscriptions.Add(new DataSubscription(PerpAsset, OneMinute));
        strategy.OnBarCompleteAction = (bar, sub, orders) =>
        {
            if (strategy.Feeds!.HasNewData("funding") &&
                strategy.Feeds.TryGetLatest("funding", out var values))
            {
                capturedFundingRate = values[0];
            }
        };

        _engine.Run([bars], strategy, CreateOptions(), feedContext: feedContext);

        Assert.NotNull(capturedFundingRate);
        Assert.Equal(0.0001, capturedFundingRate.Value);
    }

    [Fact]
    public void Run_FeedDataAvailableBeforeOnBarStart()
    {
        // Verify data is available in OnBarStart (not just OnBarComplete)
        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 50000);

        var feedContext = new BacktestFeedContext();
        feedContext.Register("funding",
            new DataFeedSchema("funding", ["fundingRate"]),
            new FeedSeries([StartMs + StepMs], [[0.0002]]));

        bool feedAvailableInOnBarStart = false;
        var strategy = new FeedAwareTestStrategy();
        strategy.DataSubscriptions.Add(new DataSubscription(PerpAsset, OneMinute));
        strategy.OnBarStartAction = (bar, sub, orders) =>
        {
            if (strategy.Feeds!.HasNewData("funding"))
                feedAvailableInOnBarStart = true;
        };

        _engine.Run([bars], strategy, CreateOptions(), feedContext: feedContext);

        Assert.True(feedAvailableInOnBarStart);
    }

    [Fact]
    public void Run_HasNewData_FalseWhenNoNewRecordThisBar()
    {
        // 3 bars. Funding only at bar 0.
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 50000);

        var feedContext = new BacktestFeedContext();
        feedContext.Register("funding",
            new DataFeedSchema("funding", ["fundingRate"]),
            new FeedSeries([StartMs], [[0.0001]]));

        var hasNewPerBar = new List<bool>();
        var strategy = new FeedAwareTestStrategy();
        strategy.DataSubscriptions.Add(new DataSubscription(PerpAsset, OneMinute));
        strategy.OnBarCompleteAction = (bar, sub, orders) =>
        {
            hasNewPerBar.Add(strategy.Feeds!.HasNewData("funding"));
        };

        _engine.Run([bars], strategy, CreateOptions(), feedContext: feedContext);

        Assert.Equal(3, hasNewPerBar.Count);
        Assert.True(hasNewPerBar[0]);   // bar 0: funding consumed
        Assert.False(hasNewPerBar[1]);  // bar 1: no new data
        Assert.False(hasNewPerBar[2]);  // bar 2: no new data
    }

    #endregion

    #region Auto-apply funding rate

    [Fact]
    public void Run_AutoApplyFunding_AdjustsPortfolioCash()
    {
        // 3 bars at price=50000: t=0, t=60s, t=120s
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 50000, priceIncrement: 0);

        // Funding at t=120s with auto-apply
        var schema = new DataFeedSchema("funding", ["fundingRate"],
            new AutoApplyConfig(AutoApplyType.FundingRate, "fundingRate"));
        var feedContext = new BacktestFeedContext();
        feedContext.Register("funding", schema,
            new FeedSeries([StartMs + 2 * StepMs], [[0.0001]]),
            asset: PerpAsset);

        // Strategy places a market buy on OnBarStart of bar 0
        // → fills same bar → position exists when funding is applied at bar 2
        _barMatcher.GetFillPrice(Arg.Any<Order>(), Arg.Any<Int64Bar>(), Arg.Any<BacktestOptions>())
            .Returns(50000L);

        var strategy = new FeedAwareTestStrategy();
        strategy.DataSubscriptions.Add(new DataSubscription(PerpAsset, OneMinute));
        var orderPlaced = false;
        strategy.OnBarStartAction = (bar, sub, orders) =>
        {
            if (!orderPlaced)
            {
                orders.Submit(new Order { Id = 0, Asset = PerpAsset, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 1m });
                orderPlaced = true;
            }
        };

        var result = _engine.Run([bars], strategy, CreateOptions(), feedContext: feedContext);

        // Initial cash: 1,000,000
        // After margin open: cash -= commission (0) only → 1,000,000
        // At bar 2: funding = -(1 × 50000 × 0.0001 × 1) = -5 ticks → cash = 999,995
        // Plus unrealized PnL from position (price unchanged → 0)
        Assert.Equal(999_995L, result.FinalPortfolio.Cash);
    }

    [Fact]
    public void Run_AutoApplyFunding_ShortPositionReceivesFunding()
    {
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 50000, priceIncrement: 0);

        var schema = new DataFeedSchema("funding", ["fundingRate"],
            new AutoApplyConfig(AutoApplyType.FundingRate, "fundingRate"));
        var feedContext = new BacktestFeedContext();
        feedContext.Register("funding", schema,
            new FeedSeries([StartMs + 2 * StepMs], [[0.0001]]),
            asset: PerpAsset);

        _barMatcher.GetFillPrice(Arg.Any<Order>(), Arg.Any<Int64Bar>(), Arg.Any<BacktestOptions>())
            .Returns(50000L);

        var strategy = new FeedAwareTestStrategy();
        strategy.DataSubscriptions.Add(new DataSubscription(PerpAsset, OneMinute));
        var orderPlaced = false;
        strategy.OnBarStartAction = (bar, sub, orders) =>
        {
            if (!orderPlaced)
            {
                orders.Submit(new Order { Id = 0, Asset = PerpAsset, Side = OrderSide.Sell, Type = OrderType.Market, Quantity = 1m });
                orderPlaced = true;
            }
        };

        var result = _engine.Run([bars], strategy, CreateOptions(), feedContext: feedContext);

        // Short position with positive funding rate → receives funding
        // delta = -(-1 × 50000 × 0.0001 × 1) = +5
        Assert.Equal(1_000_005L, result.FinalPortfolio.Cash);
    }

    [Fact]
    public void Run_AutoApplyFunding_NoPosition_NoCashChange()
    {
        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 50000);

        var schema = new DataFeedSchema("funding", ["fundingRate"],
            new AutoApplyConfig(AutoApplyType.FundingRate, "fundingRate"));
        var feedContext = new BacktestFeedContext();
        feedContext.Register("funding", schema,
            new FeedSeries([StartMs + StepMs], [[0.0001]]),
            asset: PerpAsset);

        var strategy = new FeedAwareTestStrategy();
        strategy.DataSubscriptions.Add(new DataSubscription(PerpAsset, OneMinute));

        var result = _engine.Run([bars], strategy, CreateOptions(), feedContext: feedContext);

        Assert.Equal(1_000_000L, result.FinalPortfolio.Cash);
    }

    [Fact]
    public void Run_MultipleFundingEvents_CumulativeEffect()
    {
        // 4 bars. Funding at bar 1 and bar 3.
        var bars = TestBars.CreateSeries(Start, OneMinute, 4, startPrice: 50000, priceIncrement: 0);

        var schema = new DataFeedSchema("funding", ["fundingRate"],
            new AutoApplyConfig(AutoApplyType.FundingRate, "fundingRate"));
        var feedContext = new BacktestFeedContext();
        feedContext.Register("funding", schema,
            new FeedSeries(
                [StartMs + StepMs, StartMs + 3 * StepMs],
                [[0.0001, -0.0002]]),
            asset: PerpAsset);

        _barMatcher.GetFillPrice(Arg.Any<Order>(), Arg.Any<Int64Bar>(), Arg.Any<BacktestOptions>())
            .Returns(50000L);

        var strategy = new FeedAwareTestStrategy();
        strategy.DataSubscriptions.Add(new DataSubscription(PerpAsset, OneMinute));
        var orderPlaced = false;
        strategy.OnBarStartAction = (bar, sub, orders) =>
        {
            if (!orderPlaced)
            {
                orders.Submit(new Order { Id = 0, Asset = PerpAsset, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 1m });
                orderPlaced = true;
            }
        };

        var result = _engine.Run([bars], strategy, CreateOptions(), feedContext: feedContext);

        // Bar 0: open long. Cash = 1,000,000 (margin, no commission)
        // Bar 1: funding +0.0001 → delta = -(1 × 50000 × 0.0001) = -5 → cash = 999,995
        // Bar 3: funding -0.0002 → delta = -(1 × 50000 × -0.0002) = +10 → cash = 1,000,005
        Assert.Equal(1_000_005L, result.FinalPortfolio.Cash);
    }

    #endregion

    #region No feed context — existing behavior

    [Fact]
    public void Run_WithoutFeedContext_WorksNormally()
    {
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 10000);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription> { sub });

        var result = _engine.Run([bars], strategy, CreateOptions());

        Assert.Equal(3, result.TotalBarsProcessed);
        Assert.Equal(1_000_000L, result.FinalPortfolio.Cash);
    }

    #endregion

    /// <summary>
    /// Test strategy that implements <see cref="IFeedContextReceiver"/> so the engine
    /// wires <see cref="IFeedContext"/> before <c>OnInit</c>.
    /// </summary>
    private sealed class FeedAwareTestStrategy : IInt64BarStrategy, IFeedContextReceiver, IEventBusReceiver
    {
        public string Version => "1.0";
        public IList<DataSubscription> DataSubscriptions { get; } = new List<DataSubscription>();
        public IFeedContext? Feeds { get; private set; }

        public Action<Int64Bar, DataSubscription, IOrderContext>? OnBarStartAction { get; set; }
        public Action<Int64Bar, DataSubscription, IOrderContext>? OnBarCompleteAction { get; set; }

        public void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
            => OnBarStartAction?.Invoke(bar, subscription, orders);

        public void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
            => OnBarCompleteAction?.Invoke(bar, subscription, orders);

        public void OnInit() { }
        public void OnTrade(Fill fill, Order order, IOrderContext orders) { }
        void IFeedContextReceiver.SetFeedContext(IFeedContext context) => Feeds = context;
        void IEventBusReceiver.SetEventBus(IEventBus bus) { }
    }
}
