using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

/// <summary>
/// End-to-end integration tests for crypto perpetual futures support.
/// Each test exercises the full engine pipeline: margin settlement + aux feeds + auto-apply.
/// </summary>
public class CryptoFuturesIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly long StartMs = Start.ToUnixTimeMilliseconds();
    private static readonly long StepMs = (long)OneMinute.TotalMilliseconds;

    private static BacktestEngine CreateEngine() =>
        new(new BarMatcher(), new OrderValidator());

    private static BacktestOptions DefaultOptions(long initialCash = 1_000_000L) =>
        new()
        {
            InitialCash = initialCash,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

    #region Simple perpetual long: open $50k, close $51k

    [Fact]
    public void PerpLong_OpenClose_CorrectPnl()
    {
        // Arrange: 3 bars. Bar 0: open=50000. Bar 1: stays. Bar 2: close price=51000.
        var asset = TestAssets.BtcUsdtPerp;
        var bars = new TimeSeries<Int64Bar>();
        bars.Add(TestBars.Create(50_000, 50_500, 49_500, 50_000, timestampMs: StartMs));
        bars.Add(TestBars.Create(50_000, 50_500, 49_500, 50_200, timestampMs: StartMs + StepMs));
        bars.Add(TestBars.Create(51_000, 51_500, 50_500, 51_000, timestampMs: StartMs + 2 * StepMs));

        var strategy = new SimpleEntryExitStrategy(asset, buyOnBar: 0, sellOnBar: 2);

        // Act
        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        // Assert
        // Margin settlement: open deducts no notional, close realizes PnL
        // Fill at open price. Bar 0 open=50000, Bar 2 open=51000.
        // PnL = (51000 - 50000) × 1 × 1 = 1000
        // Cash = 1_000_000 + 1000 = 1_001_000 (no commission in this test)
        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(1_001_000L, result.FinalPortfolio.Cash);
    }

    #endregion

    #region Perpetual short: open $50k, close $49k

    [Fact]
    public void PerpShort_OpenClose_CorrectPnl()
    {
        var asset = TestAssets.BtcUsdtPerp;
        var bars = new TimeSeries<Int64Bar>();
        bars.Add(TestBars.Create(50_000, 50_500, 49_500, 50_000, timestampMs: StartMs));
        bars.Add(TestBars.Create(49_500, 50_000, 49_000, 49_200, timestampMs: StartMs + StepMs));
        bars.Add(TestBars.Create(49_000, 49_500, 48_500, 49_000, timestampMs: StartMs + 2 * StepMs));

        var strategy = new SimpleEntryExitStrategy(asset, sellOnBar: 0, buyOnBar: 2);

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        // Short sell at 50000, buy to close at 49000
        // PnL = (50000 - 49000) × 1 × 1 = 1000
        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(1_001_000L, result.FinalPortfolio.Cash);
    }

    #endregion

    #region Funding rate across 3 intervals

    [Fact]
    public void FundingRate_ThreeIntervals_CumulativeCashAdjustment()
    {
        var asset = TestAssets.BtcUsdtPerp;

        // 5 bars at constant price=50000
        var bars = new TimeSeries<Int64Bar>();
        for (var i = 0; i < 5; i++)
            bars.Add(TestBars.Create(50_000, 50_500, 49_500, 50_000, timestampMs: StartMs + i * StepMs));

        // Funding at bars 1, 3, 4 with different rates
        var schema = new DataFeedSchema("funding", ["fundingRate"],
            new AutoApplyConfig(AutoApplyType.FundingRate, "fundingRate"));
        var feedContext = new BacktestFeedContext();
        feedContext.Register("funding", schema,
            new FeedSeries(
                [StartMs + StepMs, StartMs + 3 * StepMs, StartMs + 4 * StepMs],
                [[0.0001, -0.0002, 0.0001]]),
            asset: asset);

        // Strategy opens long on bar 0 (order fills at bar open = 50000)
        var strategy = new SimpleEntryExitStrategy(asset, buyOnBar: 0);

        var result = CreateEngine().Run([bars], strategy, DefaultOptions(), feedContext: feedContext);

        // Cash adjustments:
        // Bar 1: delta = -(1 × 50000 × 0.0001 × 1) = -5 → cash = 999_995
        // Bar 3: delta = -(1 × 50000 × -0.0002 × 1) = +10 → cash = 1_000_005
        // Bar 4: delta = -(1 × 50000 × 0.0001 × 1) = -5 → cash = 1_000_000
        // Net funding = -5 + 10 - 5 = 0
        Assert.Equal(1_000_000L, result.FinalPortfolio.Cash);
    }

    #endregion

    #region Mixed spot + futures portfolio

    [Fact]
    public void MixedPortfolio_SpotAndPerp_CorrectEquity()
    {
        var spotAsset = TestAssets.BtcUsdt;
        var perpAsset = TestAssets.BtcUsdtPerp;

        // Two subscriptions: spot 1m and perp 1m, synchronized timestamps
        var spotBars = new TimeSeries<Int64Bar>();
        spotBars.Add(TestBars.Create(5_000_000, 5_050_000, 4_950_000, 5_000_000, timestampMs: StartMs));
        spotBars.Add(TestBars.Create(5_100_000, 5_150_000, 5_050_000, 5_100_000, timestampMs: StartMs + StepMs));

        var perpBars = new TimeSeries<Int64Bar>();
        perpBars.Add(TestBars.Create(50_000, 50_500, 49_500, 50_000, timestampMs: StartMs));
        perpBars.Add(TestBars.Create(51_000, 51_500, 50_500, 51_000, timestampMs: StartMs + StepMs));

        // Strategy buys spot and perp on bar 0
        var strategy = new DualAssetStrategy(spotAsset, perpAsset);

        var result = CreateEngine().Run(
            [spotBars, perpBars], strategy,
            DefaultOptions(initialCash: 10_000_000L));

        // Spot buy at 5_000_000 → cash -= 5_000_000 = 5_000_000
        // Perp buy at 50_000 → cash unchanged (margin)
        // Final equity at spot=5_100_000, perp=51_000:
        //   Cash = 5_000_000
        //   Spot position value = 1 × 5_100_000 × 1 = 5_100_000
        //   Perp position value = unrealizedPnL = (51000-50000) × 1 × 1 = 1000
        //   Equity = 5_000_000 + 5_100_000 + 1_000 = 10_101_000
        var lastEquity = result.EquityCurve[^1].Value;
        Assert.Equal(10_101_000L, lastEquity);
    }

    #endregion

    #region Margin rejection

    [Fact]
    public void MarginRejection_InsufficientMargin_OrderRejected()
    {
        // Perp with 10% margin. At price 50000, margin = 5000. Start with only 4000 cash.
        var asset = TestAssets.BtcUsdtPerp;
        var bars = new TimeSeries<Int64Bar>();
        bars.Add(TestBars.Create(50_000, 50_500, 49_500, 50_000, timestampMs: StartMs));
        bars.Add(TestBars.Create(50_100, 50_600, 49_600, 50_100, timestampMs: StartMs + StepMs));

        var strategy = new SimpleEntryExitStrategy(asset, buyOnBar: 0);

        var result = CreateEngine().Run([bars], strategy, DefaultOptions(initialCash: 4_000L));

        // Order should be rejected: margin 5000 > cash 4000
        Assert.Empty(result.Fills);
        Assert.Equal(4_000L, result.FinalPortfolio.Cash);
    }

    #endregion

    #region Multiple aux feeds (funding + OI + taker vol)

    [Fact]
    public void MultipleFeedTypes_AllAccessibleDuringOnBarComplete()
    {
        var asset = TestAssets.BtcUsdtPerp;
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 50000);

        var feedContext = new BacktestFeedContext();

        // Funding feed: 1 record at bar 1
        feedContext.Register("funding",
            new DataFeedSchema("funding", ["fundingRate", "markPrice"]),
            new FeedSeries([StartMs + StepMs], [[0.0001], [50100.0]]));

        // OI feed: 2 records at bars 0 and 2
        feedContext.Register("oi",
            new DataFeedSchema("oi", ["sumOI", "sumOI_USD"]),
            new FeedSeries([StartMs, StartMs + 2 * StepMs], [[100000.0, 150000.0], [5e9, 7.5e9]]));

        // Taker volume: 1 record at bar 1
        feedContext.Register("taker_vol",
            new DataFeedSchema("taker_vol", ["buyVol", "sellVol", "buySellRatio"]),
            new FeedSeries([StartMs + StepMs], [[500.0], [450.0], [1.11]]));

        var captures = new Dictionary<string, double[]>();
        var strategy = new FeedQueryStrategy(asset, (feeds, barIndex) =>
        {
            if (barIndex == 1)
            {
                if (feeds.TryGetLatest("funding", out var fv))
                    captures["funding"] = (double[])fv.Clone();
                if (feeds.TryGetLatest("oi", out var oiv))
                    captures["oi"] = (double[])oiv.Clone();
                if (feeds.TryGetLatest("taker_vol", out var tv))
                    captures["taker_vol"] = (double[])tv.Clone();
            }
        });

        CreateEngine().Run([bars], strategy, DefaultOptions(), feedContext: feedContext);

        // All 3 feeds accessible at bar 1
        Assert.Equal(3, captures.Count);
        Assert.Equal(0.0001, captures["funding"][0]);     // fundingRate
        Assert.Equal(50100.0, captures["funding"][1]);    // markPrice
        Assert.Equal(100000.0, captures["oi"][0]);        // sumOI (from bar 0 record)
        Assert.Equal(500.0, captures["taker_vol"][0]);    // buyVol
        Assert.Equal(1.11, captures["taker_vol"][2]);     // buySellRatio
    }

    #endregion

    #region CryptoPerpetual factory smoke test

    [Fact]
    public void CryptoPerpetualAsset_HasCorrectProperties()
    {
        var asset = CryptoPerpetualAsset.Create("ETHUSDT_PERP", "Binance", decimalDigits: 2, margin: 0.05m);

        Assert.IsType<CryptoPerpetualAsset>(asset);
        Assert.Equal(SettlementMode.Margin, asset.Settlement);
        Assert.Equal(1m, asset.Multiplier);
        Assert.Equal(0.01m, asset.TickSize);
        Assert.Equal(0.05m, asset.MarginRequirement);
    }

    [Fact]
    public void StockFuture_DelegatesToFuture()
    {
        var asset = FutureAsset.Create("ES", "CME", multiplier: 50m, tickSize: 0.25m, margin: 0.05m);

        Assert.IsType<FutureAsset>(asset);
        Assert.Equal(SettlementMode.Margin, asset.Settlement);
        Assert.Equal(50m, asset.Multiplier);
        Assert.Equal(0.25m, asset.TickSize);
    }

    #endregion

    #region Test helpers

    /// <summary>
    /// Opens a position on one bar and optionally closes on another.
    /// Uses market orders filled at bar open price.
    /// </summary>
    private sealed class SimpleEntryExitStrategy : IInt64BarStrategy, IFeedContextReceiver, IEventBusReceiver
    {
        private readonly Asset _asset;
        private readonly int _buyOnBar;
        private readonly int _sellOnBar;
        private int _barIndex;

        public SimpleEntryExitStrategy(Asset asset, int buyOnBar = -1, int sellOnBar = -1)
        {
            _asset = asset;
            _buyOnBar = buyOnBar;
            _sellOnBar = sellOnBar;
            DataSubscriptions.Add(new DataSubscription(asset, TimeSpan.FromMinutes(1)));
        }

        public string Version => "1.0";
        public IList<DataSubscription> DataSubscriptions { get; } = new List<DataSubscription>();

        public void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            if (_barIndex == _buyOnBar)
                orders.Submit(new Order { Id = 0, Asset = _asset, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 1m });
            if (_barIndex == _sellOnBar)
                orders.Submit(new Order { Id = 0, Asset = _asset, Side = OrderSide.Sell, Type = OrderType.Market, Quantity = 1m });
        }

        public void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders) =>
            _barIndex++;

        public void OnInit() { }
        public void OnTrade(Fill fill, Order order, IOrderContext orders) { }
        void IFeedContextReceiver.SetFeedContext(IFeedContext context) { }
        void IEventBusReceiver.SetEventBus(IEventBus bus) { }
    }

    /// <summary>
    /// Buys both spot and perp on bar 0. Two subscriptions.
    /// </summary>
    private sealed class DualAssetStrategy : IInt64BarStrategy, IFeedContextReceiver, IEventBusReceiver
    {
        private readonly Asset _spotAsset;
        private readonly Asset _perpAsset;
        private bool _ordered;

        public DualAssetStrategy(Asset spotAsset, Asset perpAsset)
        {
            _spotAsset = spotAsset;
            _perpAsset = perpAsset;
            DataSubscriptions.Add(new DataSubscription(spotAsset, TimeSpan.FromMinutes(1)));
            DataSubscriptions.Add(new DataSubscription(perpAsset, TimeSpan.FromMinutes(1)));
        }

        public string Version => "1.0";
        public IList<DataSubscription> DataSubscriptions { get; } = new List<DataSubscription>();

        public void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            if (_ordered) return;
            orders.Submit(new Order { Id = 0, Asset = _spotAsset, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 1m });
            orders.Submit(new Order { Id = 0, Asset = _perpAsset, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 1m });
            _ordered = true;
        }

        public void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order, IOrderContext orders) { }
        void IFeedContextReceiver.SetFeedContext(IFeedContext context) { }
        void IEventBusReceiver.SetEventBus(IEventBus bus) { }
    }

    /// <summary>
    /// Strategy that queries feeds during OnBarComplete via a callback.
    /// </summary>
    private sealed class FeedQueryStrategy : IInt64BarStrategy, IFeedContextReceiver, IEventBusReceiver
    {
        private readonly Action<IFeedContext, int> _onBar;
        private IFeedContext _feeds = NullFeedContext.Instance;
        private int _barIndex;

        public FeedQueryStrategy(Asset asset, Action<IFeedContext, int> onBar)
        {
            _onBar = onBar;
            DataSubscriptions.Add(new DataSubscription(asset, TimeSpan.FromMinutes(1)));
        }

        public string Version => "1.0";
        public IList<DataSubscription> DataSubscriptions { get; } = new List<DataSubscription>();

        public void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders) { }
        public void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            _onBar(_feeds, _barIndex);
            _barIndex++;
        }

        public void OnInit() { }
        public void OnTrade(Fill fill, Order order, IOrderContext orders) { }
        void IFeedContextReceiver.SetFeedContext(IFeedContext context) => _feeds = context;
        void IEventBusReceiver.SetEventBus(IEventBus bus) { }
    }

    #endregion
}
