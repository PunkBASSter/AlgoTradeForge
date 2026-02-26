using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Events;

public class EngineEventEmissionTests
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

    // ── run.start / bar / run.end ordering ──────────────────────────────

    [Fact]
    public void EmptyData_EmitsRunStartAndRunEnd()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = new ActionStrategy(sub);
        var empty = new TimeSeries<Int64Bar>();

        CreateEngine().Run([empty], strategy, CreateOptions(), bus: bus);

        Assert.Equal(2, bus.Events.Count);
        Assert.IsType<RunStartEvent>(bus.Events[0]);
        Assert.IsType<RunEndEvent>(bus.Events[1]);
    }

    [Fact]
    public void NonEmptyData_EmitsRunStart_Bars_RunEnd()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = new ActionStrategy(sub);
        var bars = TestBars.CreateSeries(Start, OneMinute, 3);

        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        Assert.IsType<RunStartEvent>(bus.Events[0]);
        Assert.IsType<RunEndEvent>(bus.Events[^1]);

        var barEvents = bus.Events.OfType<BarEvent>().ToList();
        Assert.Equal(3, barEvents.Count);
    }

    // ── ord.place ────────────────────────────────────────────────────────

    [Fact]
    public void OrderSubmission_EmitsOrdPlace()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new ActionStrategy(sub)
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
        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        var place = Assert.Single(bus.Events.OfType<OrderPlaceEvent>());
        Assert.Equal("AAPL", place.AssetName);
        Assert.Equal(OrderSide.Buy, place.Side);
        Assert.Equal(OrderType.Market, place.Type);
        Assert.Equal(1m, place.Quantity);
    }

    // ── ord.fill + pos ──────────────────────────────────────────────────

    [Fact]
    public void OrderFill_EmitsOrdFillAndPos()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new ActionStrategy(sub)
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
                    Quantity = 5m
                });
            }
        };

        var bars = TestBars.CreateSeries(Start, OneMinute, 1, startPrice: 1000);
        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        var fill = Assert.Single(bus.Events.OfType<OrderFillEvent>());
        Assert.Equal("AAPL", fill.AssetName);
        Assert.Equal(OrderSide.Buy, fill.Side);
        Assert.Equal(5m, fill.Quantity);
        Assert.Equal(1000L, fill.Price);

        var pos = Assert.Single(bus.Events.OfType<PositionEvent>());
        Assert.Equal("AAPL", pos.AssetName);
        Assert.Equal(5m, pos.Quantity);
        Assert.Equal(1000L, pos.AverageEntryPrice);
    }

    // ── risk pass ───────────────────────────────────────────────────────

    [Fact]
    public void SuccessfulFill_EmitsRiskPassedTrue()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new ActionStrategy(sub)
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
        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        var risk = Assert.Single(bus.Events.OfType<RiskEvent>());
        Assert.True(risk.Passed);
        Assert.Equal("CashCheck", risk.CheckName);
        Assert.Null(risk.Reason);
    }

    // ── risk fail + ord.reject + warn ───────────────────────────────────

    [Fact]
    public void InsufficientCash_EmitsRiskFail_OrdReject_Warning()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new ActionStrategy(sub)
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
                    Quantity = 1000m // far exceeds $100K
                });
            }
        };

        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 15000);
        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        var risk = Assert.Single(bus.Events.OfType<RiskEvent>());
        Assert.False(risk.Passed);
        Assert.Equal("Insufficient cash", risk.Reason);

        var reject = Assert.Single(bus.Events.OfType<OrderRejectEvent>());
        Assert.Equal("Insufficient cash", reject.Reason);

        var warn = Assert.Single(bus.Events.OfType<WarningEvent>());
        Assert.Contains("rejected", warn.Message);
    }

    // ── ord.cancel ──────────────────────────────────────────────────────

    [Fact]
    public void StrategyCancelsOrder_EmitsOrdCancel()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var barCount = 0;

        var strategy = new ActionStrategy(sub)
        {
            OnBarCompleteAction = (_, _, orders) =>
            {
                barCount++;
                if (barCount == 1)
                {
                    orders.Submit(new Order
                    {
                        Id = 0,
                        Asset = TestAssets.Aapl,
                        Side = OrderSide.Buy,
                        Type = OrderType.Limit,
                        Quantity = 1m,
                        LimitPrice = 1L // will never fill
                    });
                }
                else if (barCount == 2)
                {
                    // Engine has assigned the real ID by now — retrieve it
                    var pending = orders.GetPendingOrders();
                    if (pending.Count > 0)
                        orders.Cancel(pending[0].Id);
                }
            }
        };

        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 10000);
        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        var cancel = Assert.Single(bus.Events.OfType<OrderCancelEvent>());
        Assert.Equal("AAPL", cancel.AssetName);
        Assert.Equal("Strategy cancelled", cancel.Reason);
    }

    // ── SL/TP triggers emit ord.fill + pos ──────────────────────────────

    [Fact]
    public void SlTpTrigger_EmitsOrdFillAndPos()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new ActionStrategy(sub)
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

        // Bar 0: O=10000, H=11000, L=8500, C=9000 → entry at 10000, SL at 9000 triggers
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(10000, 11000, 8500, 9000));

        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        var fills = bus.Events.OfType<OrderFillEvent>().ToList();
        Assert.Equal(2, fills.Count);
        Assert.Equal(OrderSide.Buy, fills[0].Side);
        Assert.Equal(OrderSide.Sell, fills[1].Side);
        Assert.Equal(9000L, fills[1].Price);

        var positions = bus.Events.OfType<PositionEvent>().ToList();
        Assert.Equal(2, positions.Count);
        Assert.Equal(0m, positions[1].Quantity); // closed
    }

    // ── Position lifecycle: open → increase → close ─────────────────────

    [Fact]
    public void PositionLifecycle_Open_Increase_Close()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var barCount = 0;

        var strategy = new ActionStrategy(sub)
        {
            OnBarStartAction = (_, _, orders) =>
            {
                barCount++;
                switch (barCount)
                {
                    case 1: // Open: buy 2
                        orders.Submit(new Order
                        {
                            Id = 0, Asset = TestAssets.Aapl,
                            Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 2m
                        });
                        break;
                    case 2: // Increase: buy 3 more
                        orders.Submit(new Order
                        {
                            Id = 0, Asset = TestAssets.Aapl,
                            Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 3m
                        });
                        break;
                    case 3: // Close: sell all 5
                        orders.Submit(new Order
                        {
                            Id = 0, Asset = TestAssets.Aapl,
                            Side = OrderSide.Sell, Type = OrderType.Market, Quantity = 5m
                        });
                        break;
                }
            }
        };

        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 1000);
        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        var positions = bus.Events.OfType<PositionEvent>().ToList();
        Assert.Equal(3, positions.Count);

        // Open: qty 2
        Assert.Equal(2m, positions[0].Quantity);
        Assert.Equal(1000L, positions[0].AverageEntryPrice);

        // Increase: qty 5 (2 + 3), blended average entry
        Assert.Equal(5m, positions[1].Quantity);
        Assert.True(positions[1].AverageEntryPrice > 0);

        // Close: qty 0, realized PnL recorded
        Assert.Equal(0m, positions[2].Quantity);
    }

    // ── Full event stream ordering ──────────────────────────────────────

    [Fact]
    public void FullRun_EventStreamOrdering()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new ActionStrategy(sub)
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

        // 2 bars: order placed on bar 0, filled on bar 0 (same bar via OnBarStart)
        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 1000);
        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        var types = bus.Events.Select(e => e.GetType().Name).ToList();

        // run.start comes first
        Assert.Equal(nameof(RunStartEvent), types[0]);
        // run.end comes last
        Assert.Equal(nameof(RunEndEvent), types[^1]);

        // ord.place comes before ord.fill
        var placeIdx = types.IndexOf(nameof(OrderPlaceEvent));
        var fillIdx = types.IndexOf(nameof(OrderFillEvent));
        Assert.True(placeIdx < fillIdx, "ord.place should precede ord.fill");

        // risk comes before fill
        var riskIdx = types.IndexOf(nameof(RiskEvent));
        Assert.True(riskIdx < fillIdx, "risk should precede ord.fill");

        // fill comes before pos
        var posIdx = types.IndexOf(nameof(PositionEvent));
        Assert.True(fillIdx < posIdx, "ord.fill should precede pos");
    }

    // ── Error event ─────────────────────────────────────────────────────

    [Fact]
    public void StrategyException_EmitsErrorEvent()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);

        var strategy = new ActionStrategy(sub)
        {
            OnBarCompleteAction = (_, _, _) => throw new InvalidOperationException("Strategy broke")
        };

        var bars = TestBars.CreateSeries(Start, OneMinute, 1, startPrice: 1000);

        Assert.Throws<InvalidOperationException>(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus));

        var err = Assert.Single(bus.Events.OfType<ErrorEvent>());
        Assert.Equal("Strategy broke", err.Message);
        Assert.NotNull(err.StackTrace);
    }

    [Fact]
    public void Cancellation_DoesNotEmitErrorEvent()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = new ActionStrategy(sub);
        var bars = TestBars.CreateSeries(Start, OneMinute, 100);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CreateEngine().Run([bars], strategy, CreateOptions(), cts.Token, bus: bus));

        Assert.Empty(bus.Events.OfType<ErrorEvent>());
    }

    // ── NullEventBus path ───────────────────────────────────────────────

    [Fact]
    public void DefaultNullBus_NoEventsEmitted()
    {
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = new ActionStrategy(sub);
        var bars = TestBars.CreateSeries(Start, OneMinute, 3);

        // No bus param → NullEventBus, should run without error
        var result = CreateEngine().Run([bars], strategy, CreateOptions());

        Assert.Equal(3, result.TotalBarsProcessed);
    }

    // ── RunStartEvent field validation ──────────────────────────────────

    [Fact]
    public void RunStartEvent_ContainsCorrectFields()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = new ActionStrategy(sub);
        var empty = new TimeSeries<Int64Bar>();
        var opts = CreateOptions(initialCash: 50_000L);

        CreateEngine().Run([empty], strategy, opts, bus: bus);

        var evt = Assert.Single(bus.Events.OfType<RunStartEvent>());
        Assert.Equal(nameof(ActionStrategy), evt.StrategyName);
        Assert.Equal("AAPL", evt.AssetName);
        Assert.Equal(50_000L, evt.InitialCash);
        Assert.Equal(ExportMode.Backtest, evt.RunMode);
    }

    // ── RunEndEvent field validation ────────────────────────────────────

    [Fact]
    public void RunEndEvent_ContainsCorrectFields()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var submitted = false;

        var strategy = new ActionStrategy(sub)
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

        var bars = TestBars.CreateSeries(Start, OneMinute, 3, startPrice: 1000);
        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        var evt = Assert.Single(bus.Events.OfType<RunEndEvent>());
        Assert.Equal(3, evt.TotalBarsProcessed);
        Assert.Equal(1, evt.TotalFills);
        Assert.True(evt.Duration > TimeSpan.Zero);
    }

    // ── sig event via StrategyBase.EmitSignal ───────────────────────────

    [Fact]
    public void StrategyEmitSignal_EndToEnd_CapturedByBus()
    {
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        var strategy = new SignalEmittingTestStrategy(new SignalTestParams { DataSubscriptions = [sub] });
        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 1000);

        CreateEngine().Run([bars], strategy, CreateOptions(), bus: bus);

        var signals = bus.Events.OfType<SignalEvent>().ToList();
        Assert.Equal(2, signals.Count); // one per bar
        Assert.Equal("CrossUp", signals[0].SignalName);
        Assert.Equal("AAPL", signals[0].AssetName);
        Assert.Equal("Long", signals[0].Direction);
        Assert.Equal(0.9m, signals[0].Strength);
        Assert.Equal("Test reason", signals[0].Reason);
        Assert.Equal(nameof(SignalEmittingTestStrategy), signals[0].Source);
    }

    // ── Quantity validation rejection events ───────────────────────────

    [Fact]
    public void QuantityBelowMin_EmitsOrdRejectAndWarning()
    {
        var asset = Asset.Equity("TEST", "TEST", minOrderQuantity: 10m, maxOrderQuantity: 1000m, quantityStepSize: 1m);
        var bus = new CapturingEventBus();
        var sub = new DataSubscription(asset, OneMinute);
        var submitted = false;

        var strategy = new ActionStrategy(sub)
        {
            OnBarCompleteAction = (_, _, orders) =>
            {
                if (submitted) return;
                submitted = true;
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = asset,
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Quantity = 3m // below min of 10
                });
            }
        };

        var opts = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = asset,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 100);
        CreateEngine().Run([bars], strategy, opts, bus: bus);

        var reject = Assert.Single(bus.Events.OfType<OrderRejectEvent>());
        Assert.Contains("below minimum", reject.Reason);

        var warn = Assert.Single(bus.Events.OfType<WarningEvent>());
        Assert.Contains("rejected", warn.Message);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed class ActionStrategy(DataSubscription subscription) : IInt64BarStrategy
    {
        public string Version => "1.0.0";
        public IList<DataSubscription> DataSubscriptions { get; } = [subscription];

        public Action<Int64Bar, DataSubscription, IOrderContext>? OnBarStartAction { get; init; }
        public Action<Int64Bar, DataSubscription, IOrderContext>? OnBarCompleteAction { get; init; }

        public void OnInit() { }

        public void OnBarStart(Int64Bar bar, DataSubscription subscription, IOrderContext orders) =>
            OnBarStartAction?.Invoke(bar, subscription, orders);

        public void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders) =>
            OnBarCompleteAction?.Invoke(bar, subscription, orders);

        public void OnTrade(Fill fill, Order order) { }
    }

    private sealed class SignalTestParams : StrategyParamsBase;

    private sealed class SignalEmittingTestStrategy(SignalTestParams p) : StrategyBase<SignalTestParams>(p)
    {
        public override string Version => "1.0.0";
        public override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            EmitSignal(bar.Timestamp, "CrossUp", subscription.Asset.Name, "Long", 0.9m, "Test reason");
        }
    }
}
