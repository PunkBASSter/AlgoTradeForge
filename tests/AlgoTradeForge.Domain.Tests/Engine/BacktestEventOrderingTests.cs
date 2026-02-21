using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class BacktestEventOrderingTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static BacktestOptions CreateOptions(long initialCash = 100_000L) =>
        new()
        {
            InitialCash = initialCash,
            Asset = TestAssets.Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

    private static BacktestEngine CreateEngine() =>
        new(new BarMatcher(), new BasicRiskEvaluator());

    [Fact]
    public void OnBarStart_ReceivesSyntheticBar_OpenOnlyAndZeroVolume()
    {
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        Int64Bar? received = null;

        var strategy = new EventRecordingStrategy(sub)
        {
            OnBarStartAction = (bar, _, _) => received = bar
        };

        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(10000, 11000, 9000, 10500, volume: 5000));

        CreateEngine().Run([bars], strategy, CreateOptions());

        Assert.NotNull(received);
        var b = received.Value;
        Assert.Equal(10000, b.Open);
        Assert.Equal(10000, b.High);
        Assert.Equal(10000, b.Low);
        Assert.Equal(10000, b.Close);
        Assert.Equal(0, b.Volume);
    }

    [Fact]
    public void EventOrder_OnBarStart_OnTrade_OnBarComplete()
    {
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new EventRecordingStrategy(sub)
        {
            OnBarStartAction = (_, _, orders) =>
            {
                if (submitted) return;
                submitted = true;
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = TestAssets.Aapl,
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Quantity = 1m
                });
            }
        };

        var bars = TestBars.CreateSeries(Start, OneMinute, 1, startPrice: 1000);

        CreateEngine().Run([bars], strategy, CreateOptions());

        Assert.Equal(
        [
            "OnInit",
            "OnBarStart:1000",
            "OnTrade:Buy:1000",
            "OnBarComplete:1000"
        ], strategy.Events);
    }

    [Fact]
    public void OnBarStart_MarketOrder_FillVisibleInOnBarComplete()
    {
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        IReadOnlyList<Fill>? fillsSeenInComplete = null;

        var strategy = new EventRecordingStrategy(sub)
        {
            OnBarStartAction = (_, _, orders) =>
            {
                if (fillsSeenInComplete != null) return;
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = TestAssets.Aapl,
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Quantity = 1m
                });
            },
            OnBarCompleteAction = (_, _, orders) =>
            {
                fillsSeenInComplete ??= orders.GetFills().ToList();
            }
        };

        var bars = TestBars.CreateSeries(Start, OneMinute, 1, startPrice: 1000);

        var result = CreateEngine().Run([bars], strategy, CreateOptions());

        Assert.Single(result.Fills);
        Assert.NotNull(fillsSeenInComplete);
        Assert.Single(fillsSeenInComplete);
        Assert.Equal(1000L, fillsSeenInComplete[0].Price);
    }

    [Fact]
    public void OnBarStart_EntryWithSl_FullLifecycleOnSameBar()
    {
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new EventRecordingStrategy(sub)
        {
            OnBarStartAction = (_, _, orders) =>
            {
                if (submitted) return;
                submitted = true;
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = TestAssets.Aapl,
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Quantity = 1m,
                    StopLossPrice = 9000L
                });
            }
        };

        // O=10000, H=11000, L=8500 → SL=9000 hit (Low <= SL)
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(10000, 11000, 8500, 9000));

        var result = CreateEngine().Run([bars], strategy, CreateOptions());

        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(
        [
            "OnInit",
            "OnBarStart:10000",
            "OnTrade:Buy:10000",
            "OnTrade:Sell:9000",
            "OnBarComplete:10000"
        ], strategy.Events);
    }

    [Fact]
    public void TwoOrdersFillOnSameBar_OnTradeFiresInSubmissionOrder()
    {
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new EventRecordingStrategy(sub)
        {
            OnBarCompleteAction = (_, _, orders) =>
            {
                if (submitted) return;
                submitted = true;
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = TestAssets.Aapl,
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Quantity = 1m
                });
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = TestAssets.Aapl,
                    Side = OrderSide.Sell,
                    Type = OrderType.Market,
                    Quantity = 2m
                });
            }
        };

        // Bar 0: submit both orders in OnBarComplete
        // Bar 1: O=1100 — both fill
        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 1000);

        var result = CreateEngine().Run([bars], strategy, CreateOptions());

        Assert.Equal(2, result.Fills.Count);

        var buyIdx = strategy.Events.IndexOf("OnTrade:Buy:1100");
        var sellIdx = strategy.Events.IndexOf("OnTrade:Sell:1100");
        Assert.True(buyIdx >= 0, "Buy OnTrade not found");
        Assert.True(sellIdx >= 0, "Sell OnTrade not found");
        Assert.True(buyIdx < sellIdx, "Buy OnTrade should fire before Sell OnTrade (submission order)");
    }

    [Fact]
    public void OnBarComplete_OrderDoesNotFillOnSameBar()
    {
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var barCount = 0;
        IReadOnlyList<Fill>? fillsOnBar0 = null;
        IReadOnlyList<Fill>? fillsOnBar1 = null;

        var strategy = new EventRecordingStrategy(sub)
        {
            OnBarCompleteAction = (_, _, orders) =>
            {
                barCount++;
                if (barCount == 1)
                {
                    fillsOnBar0 = orders.GetFills().ToList();
                    orders.Submit(new Order
                    {
                        Id = 0,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Buy,
                        Type = OrderType.Market,
                        Quantity = 1m
                    });
                }
                else if (barCount == 2)
                {
                    fillsOnBar1 = orders.GetFills().ToList();
                }
            }
        };

        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 1000);

        CreateEngine().Run([bars], strategy, CreateOptions());

        Assert.NotNull(fillsOnBar0);
        Assert.Empty(fillsOnBar0);
        Assert.NotNull(fillsOnBar1);
        Assert.Single(fillsOnBar1);
    }

    [Fact]
    public void TwoPendingBuys_SecondRejectedDueToReducedCash()
    {
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new EventRecordingStrategy(sub)
        {
            OnBarCompleteAction = (_, _, orders) =>
            {
                if (submitted) return;
                submitted = true;
                // Two buys at 7 shares each; bar 1 Open=10100 → cost 70,700 per order
                // First fills (100K → ~29K), second rejected (70K > 29K)
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = TestAssets.Aapl,
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Quantity = 7m
                });
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = TestAssets.Aapl,
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Quantity = 7m
                });
            }
        };

        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 10000);

        var result = CreateEngine().Run([bars], strategy, CreateOptions());

        Assert.Single(result.Fills);
        Assert.Equal(7m, result.Fills[0].Quantity);
    }

    private sealed class EventRecordingStrategy(DataSubscription subscription) : IInt64BarStrategy
    {
        public string Version => "1.0.0";
        public IList<DataSubscription> DataSubscriptions { get; } = [subscription];
        public List<string> Events { get; } = [];

        public Action<Int64Bar, DataSubscription, IOrderContext>? OnBarStartAction { get; init; }
        public Action<Int64Bar, DataSubscription, IOrderContext>? OnBarCompleteAction { get; init; }

        public void OnInit() => Events.Add("OnInit");

        public void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            Events.Add($"OnBarStart:{bar.Open}");
            OnBarStartAction?.Invoke(bar, subscription, orders);
        }

        public void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            Events.Add($"OnBarComplete:{bar.Open}");
            OnBarCompleteAction?.Invoke(bar, subscription, orders);
        }

        public void OnTrade(Fill fill, Order order) =>
            Events.Add($"OnTrade:{fill.Side}:{fill.Price}");
    }
}
