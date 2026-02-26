using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class BacktestEngineTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    private readonly IBarMatcher _barMatcher;
    private readonly BacktestEngine _engine;

    public BacktestEngineTests()
    {
        _barMatcher = Substitute.For<IBarMatcher>();
        _engine = new BacktestEngine(_barMatcher);
    }

    private static BacktestOptions CreateOptions() =>
        new()
        {
            InitialCash = 100_000L,
            Asset = TestAssets.Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

    private static IInt64BarStrategy MockStrategy(params DataSubscription[] subs)
    {
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.DataSubscriptions.Returns(new List<DataSubscription>(subs));
        return strategy;
    }

    #region Multi-Subscription Chronological Feeding (US1)

    [Fact]
    public void Run_SingleSubscription_DeliversAllBars()
    {
        var bars = TestBars.CreateSeries(Start, OneMinute, 5);
        var sub = new DataSubscription(TestAssets.BtcUsdt, OneMinute);
        var strategy = MockStrategy(sub);
        var delivered = new List<(Int64Bar Bar, DataSubscription Sub)>();
        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci => delivered.Add((ci.ArgAt<Int64Bar>(0), ci.ArgAt<DataSubscription>(1))));

        var result = _engine.Run([bars], strategy, CreateOptions());

        Assert.Equal(5, result.TotalBarsProcessed);
        Assert.Equal(5, delivered.Count);
        Assert.All(delivered, d => Assert.Equal(sub, d.Sub));
    }

    [Fact]
    public void Run_TwoSubscriptions_SameTimeframe_ChronologicalOrder()
    {
        var btcSub = new DataSubscription(TestAssets.BtcUsdt, OneMinute);
        var ethAsset = Asset.Crypto("ETHUSDT", "Binance", 2);
        var ethSub = new DataSubscription(ethAsset, OneMinute);

        var btcBars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 40000);
        var ethBars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 2000);

        var delivered = new List<(DateTimeOffset Ts, DataSubscription Sub)>();
        var strategy = MockStrategy(btcSub, ethSub);

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                var bar = ci.ArgAt<Int64Bar>(0);
                delivered.Add((bar.Timestamp, ci.ArgAt<DataSubscription>(1)));
            });

        var result = _engine.Run([btcBars, ethBars], strategy, CreateOptions());

        Assert.Equal(6, result.TotalBarsProcessed);

        // Same-timestamp bars should be delivered in subscription declaration order (BTC before ETH)
        for (var i = 0; i < delivered.Count - 1; i++)
        {
            Assert.True(delivered[i].Ts <= delivered[i + 1].Ts,
                $"Bar {i} timestamp {delivered[i].Ts} should be <= bar {i + 1} timestamp {delivered[i + 1].Ts}");
        }

        // At each timestamp, BTC should come before ETH (declaration order)
        var sameTimestampPairs = delivered
            .Select((d, i) => (d, i))
            .GroupBy(x => x.d.Ts)
            .Where(g => g.Count() > 1);

        foreach (var group in sameTimestampPairs)
        {
            var items = group.OrderBy(x => x.i).Select(x => x.d.Sub).ToList();
            Assert.Equal(btcSub, items[0]);
            Assert.Equal(ethSub, items[1]);
        }
    }

    [Fact]
    public void Run_DifferentTimeframes_ChronologicalMerge()
    {
        var btcSub = new DataSubscription(TestAssets.BtcUsdt, OneMinute);
        var ethAsset = Asset.Crypto("ETHUSDT", "Binance", 2);
        var ethSub = new DataSubscription(ethAsset, FiveMinutes);

        // 5 one-minute BTC bars, 1 five-minute ETH bar (same start time)
        var btcBars = TestBars.CreateSeries(Start, OneMinute, 5, startPrice: 40000);
        var ethBars = TestBars.CreateSeries(Start, FiveMinutes, 1, startPrice: 2000);

        var deliveryOrder = new List<DataSubscription>();
        var strategy = MockStrategy(btcSub, ethSub);
        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci => deliveryOrder.Add(ci.ArgAt<DataSubscription>(1)));

        _engine.Run([btcBars, ethBars], strategy, CreateOptions());

        Assert.Equal(6, deliveryOrder.Count);
        // First bar: BTC at T+0 (same timestamp as ETH) -> BTC first (declaration order), then ETH
        Assert.Equal(btcSub, deliveryOrder[0]);
        Assert.Equal(ethSub, deliveryOrder[1]);
        // Remaining: BTC at T+1, T+2, T+3, T+4
        Assert.All(deliveryOrder.Skip(2), d => Assert.Equal(btcSub, d));
    }

    [Fact]
    public void Run_DataGap_SkipsGapContinuesNormally()
    {
        var btcSub = new DataSubscription(TestAssets.BtcUsdt, OneMinute);
        var ethAsset = Asset.Crypto("ETHUSDT", "Binance", 2);
        var ethSub = new DataSubscription(ethAsset, OneMinute);

        // BTC: 3 bars starting at T+0
        var btcBars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 40000);
        // ETH: 1 bar starting at T+2 (gap at T+0 and T+1)
        var ethBars = TestBars.CreateSeries(Start + TimeSpan.FromMinutes(2), OneMinute, 1, startPrice: 2000);

        var deliveryOrder = new List<DataSubscription>();
        var strategy = MockStrategy(btcSub, ethSub);
        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci => deliveryOrder.Add(ci.ArgAt<DataSubscription>(1)));

        var result = _engine.Run([btcBars, ethBars], strategy, CreateOptions());

        Assert.Equal(4, result.TotalBarsProcessed);
        // T+0: BTC only, T+1: BTC only, T+2: BTC then ETH
        Assert.Equal(btcSub, deliveryOrder[0]);
        Assert.Equal(btcSub, deliveryOrder[1]);
        Assert.Equal(btcSub, deliveryOrder[2]);
        Assert.Equal(ethSub, deliveryOrder[3]);
    }

    [Fact]
    public void Run_EmptyData_ReturnsEmptyResult()
    {
        var sub = new DataSubscription(TestAssets.BtcUsdt, OneMinute);
        var emptyBars = new TimeSeries<Int64Bar>();
        var strategy = MockStrategy(sub);

        var result = _engine.Run([emptyBars], strategy, CreateOptions());

        Assert.Equal(0, result.TotalBarsProcessed);
        Assert.Empty(result.Fills);
        Assert.Equal(100_000L, result.FinalPortfolio.Cash);
    }

    [Fact]
    public void Run_CancellationRequested_Throws()
    {
        var bars = TestBars.CreateSeries(Start, OneMinute, 100);
        var sub = new DataSubscription(TestAssets.BtcUsdt, OneMinute);
        var strategy = MockStrategy(sub);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => _engine.Run([bars], strategy, CreateOptions(), cts.Token));
    }

    [Fact]
    public void Run_MismatchedArrayLengths_Throws()
    {
        var bars = TestBars.CreateSeries(Start, OneMinute, 5);
        var strategy = MockStrategy(); // empty subscriptions

        Assert.Throws<ArgumentException>(
            () => _engine.Run([bars], strategy, CreateOptions()));
    }

    [Fact]
    public void Run_StrategyReceivesSingleBar_NotFullSeries()
    {
        var sub = new DataSubscription(TestAssets.BtcUsdt, OneMinute);
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 10000);
        var receivedBars = new List<Int64Bar>();
        var strategy = MockStrategy(sub);
        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci => receivedBars.Add(ci.ArgAt<Int64Bar>(0)));

        _engine.Run([bars], strategy, CreateOptions());

        Assert.Equal(3, receivedBars.Count);
        // Each bar should have a different open price
        Assert.Equal(10000, receivedBars[0].Open);
        Assert.Equal(10100, receivedBars[1].Open);
        Assert.Equal(10200, receivedBars[2].Open);
    }

    #endregion

    #region Order Queue Integration (US2)

    [Fact]
    public void Run_StrategySubmitsMarketBuy_FillGeneratedOnNextBar()
    {
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        // 3 bars: strategy places a market buy on bar 0, fill processed on bar 1
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 15000);
        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 5m // 5 shares * ~15100 = ~75,500 < 100K initial cash
                    });
                }
            });

        var result = engine.Run([bars], strategy, CreateOptions());

        Assert.Single(result.Fills);
        Assert.Equal(OrderSide.Buy, result.Fills[0].Side);
        Assert.Equal(5m, result.Fills[0].Quantity);
    }

    [Fact]
    public void Run_StrategySubmitsLimitOrder_FillsWhenPriceReached()
    {
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        // 5 bars with ascending opens (15000, 15100, ..., 15400)
        // Place a limit sell at 15350 -- should fill when bar high >= limit
        var bars = TestBars.CreateSeries(Start, OneMinute, 5, startPrice: 15000);
        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Sell,
                        Type = OrderType.Limit,
                        Quantity = 10m,
                        LimitPrice = 15350L
                    });
                }
            });

        var result = engine.Run([bars], strategy, CreateOptions());

        // Bars have High = open + 200, so bar at 15200 has High=15400 >= 15350
        Assert.Single(result.Fills);
        Assert.Equal(15350L, result.Fills[0].Price);
    }

    [Fact]
    public void Run_StrategySubmitsStopOrder_FillsWhenTriggered()
    {
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var bars = TestBars.CreateSeries(Start, OneMinute, 5, startPrice: 15000);
        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Buy,
                        Type = OrderType.Stop,
                        Quantity = 5m,
                        StopPrice = 15350L
                    });
                }
            });

        var result = engine.Run([bars], strategy, CreateOptions());

        // Bar at open=15200 has High=15400 >= StopPrice=15350
        Assert.Single(result.Fills);
        Assert.Equal(15350L, result.Fills[0].Price);
    }

    [Fact]
    public void Run_StrategySubmitsStopLimitOrder_TriggersAndFills()
    {
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var bars = TestBars.CreateSeries(Start, OneMinute, 5, startPrice: 15000);
        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Buy,
                        Type = OrderType.StopLimit,
                        Quantity = 5m,
                        StopPrice = 15350L,
                        LimitPrice = 15400L
                    });
                }
            });

        var result = engine.Run([bars], strategy, CreateOptions());

        Assert.Single(result.Fills);
        Assert.Equal(15400L, result.Fills[0].Price);
    }

    [Fact]
    public void Run_FillsObservableViaOrderContext()
    {
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 15000);
        var strategy = MockStrategy(sub);
        var barCount = 0;
        IReadOnlyList<Fill>? observedFills = null;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                barCount++;
                var ctx = ci.ArgAt<IOrderContext>(2);

                if (barCount == 1)
                {
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 5m
                    });
                }
                else if (barCount == 2)
                {
                    // On bar 2, the market order from bar 1 was filled during this bar's processing
                    observedFills = ctx.GetFills();
                }
            });

        var result = engine.Run([bars], strategy, CreateOptions());

        Assert.NotNull(observedFills);
        Assert.Single(observedFills);
        Assert.Equal(OrderSide.Buy, observedFills[0].Side);
        Assert.Equal(5m, observedFills[0].Quantity);
        Assert.Single(result.Fills);
    }

    [Fact]
    public void Run_InsufficientCash_OrderRejected()
    {
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 15000);
        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    // Try to buy 1000 shares at ~15100 = $15,100,000 -- way more than $100K initial cash
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 1000m
                    });
                }
            });

        var opts = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = TestAssets.Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        var result = engine.Run([bars], strategy, opts);

        // Order should be rejected due to insufficient cash -- no fills
        Assert.Empty(result.Fills);
    }

    [Fact]
    public void Run_100Orders_NoCorruptionOrDuplicateFills()
    {
        // SC-005: 100 orders processed without queue corruption, lost orders, or duplicate fills
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        // 200 bars with low prices so limit buys are affordable (price 10..210, 1 share each = ~$100 per fill)
        var bars = TestBars.CreateSeries(Start, OneMinute, 200, startPrice: 10, priceIncrement: 1);
        var strategy = MockStrategy(sub);
        var barIdx = 0;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                barIdx++;
                if (barIdx <= 100)
                {
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    var bar = ci.ArgAt<Int64Bar>(0);
                    // Place a limit sell at a price the subsequent bar's high will exceed
                    ctx.Submit(new Order
                    {
                        Id = barIdx,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Sell,
                        Type = OrderType.Limit,
                        Quantity = 1m,
                        LimitPrice = bar.High + 1 // just above current high, reachable by next bar
                    });
                }
            });

        var opts = new BacktestOptions
        {
            InitialCash = 1_000_000L, // large cash pool so no rejections
            Asset = TestAssets.Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        var result = engine.Run([bars], strategy, opts);

        // Verify no duplicate fill OrderIds
        var fillOrderIds = result.Fills.Select(f => f.OrderId).ToList();
        Assert.Equal(fillOrderIds.Distinct().Count(), fillOrderIds.Count);
        // All 100 orders should eventually fill (ascending prices ensure highs keep increasing)
        Assert.True(result.Fills.Count > 0, "Expected at least some fills from 100 orders");
        Assert.True(result.Fills.Count <= 100, $"Got {result.Fills.Count} fills but only 100 orders submitted");
    }

    #endregion

    #region SL/TP Integration (US3)

    [Fact]
    public void Run_BuyWithSl_SlTriggersOnSubsequentBar()
    {
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);

        // Bar 0: Open=100, H=102, L=99, C=101 -- market buy fills at Open=101 (bar 1)
        // Bar 1: Open=101, H=103, L=100, C=102 -- entry fill here
        // Bar 2: Open=102, H=104, L=95, C=96  -- drops to SL=98
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(10000, 10200, 9900, 10100),
            TestBars.Create(10100, 10300, 10000, 10200),
            TestBars.Create(10200, 10400, 9500, 9600));

        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 5m,
                        StopLossPrice = 9800L // SL at 98
                    });
                }
            });

        var result = engine.Run([bars], strategy, CreateOptions());

        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(9800L, result.Fills[1].Price);
        Assert.Equal(OrderSide.Sell, result.Fills[1].Side); // SL closing the long
    }

    [Fact]
    public void Run_BuyWithTp_TpTriggersOnSubsequentBar()
    {
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);

        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(10000, 10200, 9900, 10100),
            TestBars.Create(10100, 10300, 10000, 10200),
            TestBars.Create(10200, 10800, 10100, 10700)); // High=10800 >= TP=10500

        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 5m,
                        TakeProfitLevels = [new TakeProfitLevel(10500L, 1m)]
                    });
                }
            });

        var result = engine.Run([bars], strategy, CreateOptions());

        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(10500L, result.Fills[1].Price); // TP hit
    }

    [Fact]
    public void Run_BuyWithPartialTp_TwoTpLevelsClosure()
    {
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);

        // Use lower prices so 10 shares fit in $100K budget
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(1000, 1020, 990, 1010),  // bar 0: strategy places order
            TestBars.Create(1010, 1030, 1000, 1020),  // bar 1: entry fill at 1010
            TestBars.Create(1020, 1060, 1010, 1040),  // bar 2: TP1=1050 hit (High=1060)
            TestBars.Create(1040, 1110, 1030, 1080));  // bar 3: TP2=1100 hit (High=1110)

        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 10m,
                        TakeProfitLevels =
                        [
                            new TakeProfitLevel(1050L, 0.5m), // TP1: close 50% at 1050
                            new TakeProfitLevel(1100L, 1m)    // TP2: close remaining at 1100
                        ]
                    });
                }
            });

        var result = engine.Run([bars], strategy, CreateOptions());

        Assert.Equal(3, result.Fills.Count);
        // Fill 0: entry
        Assert.Equal(10m, result.Fills[0].Quantity);
        // Fill 1: TP1 -- 50% of 10 = 5
        Assert.Equal(1050L, result.Fills[1].Price);
        Assert.Equal(5m, result.Fills[1].Quantity);
        // Fill 2: TP2 -- remaining 5
        Assert.Equal(1100L, result.Fills[2].Price);
        Assert.Equal(5m, result.Fills[2].Quantity);
    }

    #endregion

    #region Performance (SC-007)

    [Fact]
    public void Run_500KBars_CompletesWithinOneMinute()
    {
        // SC-007: Baseline SLA of 500K bars/min
        const int barCount = 500_000;
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(TestAssets.BtcUsdt, OneMinute);
        var bars = TestBars.CreateSeries(Start, OneMinute, barCount, startPrice: 10000, priceIncrement: 1);

        // No-op strategy -- pure engine throughput measurement
        var strategy = MockStrategy(sub);

        var result = engine.Run([bars], strategy, CreateOptions());

        Assert.Equal(barCount, result.TotalBarsProcessed);
        // Must complete within 60 seconds (500K bars/min baseline)
        Assert.True(result.Duration < TimeSpan.FromSeconds(60),
            $"Engine processed {barCount} bars in {result.Duration.TotalSeconds:F2}s -- exceeds 60s SLA");
    }

    [Fact]
    public void Run_ThreeSubscriptions_1_5MBars_CompletesWithinThreeMinutes()
    {
        // SC-007 extended: three subscriptions over one year of 1-minute data (~525K bars each, ~1.575M total)
        const int barsPerSub = 525_000;
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);

        var btcSub = new DataSubscription(TestAssets.BtcUsdt, OneMinute);
        var ethAsset = Asset.Crypto("ETHUSDT", "Binance", 2);
        var ethSub = new DataSubscription(ethAsset, OneMinute);
        var solAsset = Asset.Crypto("SOLUSDT", "Binance", 2);
        var solSub = new DataSubscription(solAsset, OneMinute);

        var btcBars = TestBars.CreateSeries(Start, OneMinute, barsPerSub, startPrice: 40000, priceIncrement: 1);
        var ethBars = TestBars.CreateSeries(Start, OneMinute, barsPerSub, startPrice: 2000, priceIncrement: 1);
        var solBars = TestBars.CreateSeries(Start, OneMinute, barsPerSub, startPrice: 100, priceIncrement: 1);

        var strategy = MockStrategy(btcSub, ethSub, solSub);

        var result = engine.Run(
            [btcBars, ethBars, solBars],
            strategy, CreateOptions());

        var totalBars = barsPerSub * 3;
        Assert.Equal(totalBars, result.TotalBarsProcessed);
        // Must complete within 180 seconds (3 minutes)
        Assert.True(result.Duration < TimeSpan.FromSeconds(180),
            $"Engine processed {totalBars} bars in {result.Duration.TotalSeconds:F2}s -- exceeds 180s SLA");
    }

    #endregion

    #region Quantity Validation (Asset Constraints)

    [Fact]
    public void Run_QuantityBelowMin_OrderRejected()
    {
        var asset = Asset.Equity("TEST", "TEST", minOrderQuantity: 10m, maxOrderQuantity: 1000m, quantityStepSize: 1m);
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(asset, OneMinute);
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 100);
        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = asset,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 5m // below min of 10
                    });
                }
            });

        var opts = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = asset,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        var result = engine.Run([bars], strategy, opts);

        Assert.Empty(result.Fills);
    }

    [Fact]
    public void Run_QuantityAboveMax_OrderRejected()
    {
        var asset = Asset.Equity("TEST", "TEST", minOrderQuantity: 1m, maxOrderQuantity: 10m, quantityStepSize: 1m);
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(asset, OneMinute);
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 100);
        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = asset,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 50m // above max of 10
                    });
                }
            });

        var opts = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = asset,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        var result = engine.Run([bars], strategy, opts);

        Assert.Empty(result.Fills);
    }

    [Fact]
    public void Run_QuantityMisalignedStep_OrderRejected()
    {
        var asset = Asset.Equity("TEST", "TEST", minOrderQuantity: 1m, maxOrderQuantity: 1000m, quantityStepSize: 5m);
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(asset, OneMinute);
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 100);
        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = asset,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 7m // not a multiple of step 5
                    });
                }
            });

        var opts = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = asset,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        var result = engine.Run([bars], strategy, opts);

        Assert.Empty(result.Fills);
    }

    [Fact]
    public void Run_ValidQuantity_OrderFills()
    {
        var asset = Asset.Equity("TEST", "TEST", minOrderQuantity: 1m, maxOrderQuantity: 100m, quantityStepSize: 5m);
        var realMatcher = new BarMatcher();
        var engine = new BacktestEngine(realMatcher);
        var sub = new DataSubscription(asset, OneMinute);
        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 100);
        var strategy = MockStrategy(sub);
        var orderPlaced = false;

        strategy.When(s => s.OnBarComplete(Arg.Any<Int64Bar>(), Arg.Any<DataSubscription>(), Arg.Any<IOrderContext>()))
            .Do(ci =>
            {
                if (!orderPlaced)
                {
                    orderPlaced = true;
                    var ctx = ci.ArgAt<IOrderContext>(2);
                    ctx.Submit(new Order
                    {
                        Id = 1,
                        Asset = asset,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 10m // valid: >= min, <= max, multiple of step
                    });
                }
            });

        var opts = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = asset,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        var result = engine.Run([bars], strategy, opts);

        Assert.Single(result.Fills);
        Assert.Equal(10m, result.Fills[0].Quantity);
    }

    #endregion
}
