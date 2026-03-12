using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.TradeRegistry;

public class TradeRegistryIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly Asset TestAsset = TestAssets.Aapl;

    private static BacktestEngine CreateEngine() =>
        new(new BarMatcher(), new OrderValidator());

    private static BacktestOptions DefaultOptions(long initialCash = 1_000_000L) =>
        new()
        {
            InitialCash = initialCash,
            Asset = TestAsset,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

    // ── Helper strategy that opens a group on bar 0 and delegates fills ──

    private sealed class TestStrategy : IInt64BarStrategy, IEventBusReceiver
    {
        private readonly TradeRegistryModule _registry;
        private readonly Action<Int64Bar, DataSubscription, IOrderContext, TradeRegistryModule, int>? _onBar;
        private int _barIndex;

        public TestStrategy(
            TradeRegistryModule registry,
            Action<Int64Bar, DataSubscription, IOrderContext, TradeRegistryModule, int>? onBar = null)
        {
            _registry = registry;
            _onBar = onBar;

            // Use simulation time so event timestamps reflect backtest dates, not wall-clock
            _registry.SetClock(() => _simulationTime);
        }

        private DateTimeOffset _simulationTime = DateTimeOffset.MinValue;

        public string Version => "1.0";
        public IList<DataSubscription> DataSubscriptions { get; init; } = new List<DataSubscription>();
        public void OnInit() { }

        public void SetEventBus(IEventBus bus) => _registry.SetEventBus(bus);

        public void OnTrade(Fill fill, Order order, IOrderContext orders)
        {
            _simulationTime = fill.Timestamp;
            _registry.OnFill(fill, order, orders);
        }

        public void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            _simulationTime = DateTimeOffset.FromUnixTimeMilliseconds(bar.TimestampMs);
            _onBar?.Invoke(bar, subscription, orders, _registry, _barIndex);
            _barIndex++;
        }
    }

    // ── T13: EntryThenSlHit ─────────────────────────────────────

    [Fact]
    public void EntryThenSlHit()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? group = null;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                // Bar 0: place market buy entry with SL at 14800 and TP at 16000
                group = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 10m, slPrice: 14800,
                    tpLevels: [new TpLevel { Price = 16000, ClosurePercentage = 1.0m }]);
            }
        })
        { DataSubscriptions = [sub] };

        // Bar 0: Open=15000 (strategy places order)
        // Bar 1: Open=15000 → entry fills at 15000. SL+TP placed. Low=14700 → SL at 14800 triggers.
        // Bar 2: just padding in case
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0: order submitted
            TestBars.Create(15000, 15100, 14700, 14800),  // bar 1: entry fills at open, SL hit (low 14700 <= 14800)
            TestBars.Create(14800, 14900, 14700, 14800)); // bar 2: padding

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(group);
        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        Assert.Equal(2, result.Fills.Count); // entry + SL
        // PnL: 1 * (14800 - 15000) * 10 * 1 = -2000
        Assert.Equal(-2000, group.RealizedPnl);
    }

    // ── T14: EntryThenTpHit ─────────────────────────────────────

    [Fact]
    public void EntryThenTpHit()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? group = null;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                group = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 10m, slPrice: 14000,
                    tpLevels: [new TpLevel { Price = 15500, ClosurePercentage = 1.0m }]);
            }
        })
        { DataSubscriptions = [sub] };

        // Bar 0: order submitted
        // Bar 1: entry fills at open 15000. Protective orders placed.
        //         High=15600 >= TP=15500, but SL/TP are only just placed — they are in OnBarStart/ProcessPending context.
        //         Actually: engine fills entry at ProcessPendingOrders, then OnTrade places SL+TP.
        //         SL+TP go into queue. Engine continues to next order in pending snapshot (none).
        //         Then OnBarComplete fires. Then bar 2 processes SL/TP.
        // Bar 2: High=15600 >= TP=15500 → TP fills. Low=14500 > SL=14000 → SL not hit.
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0
            TestBars.Create(15000, 15100, 14900, 15000),  // bar 1: entry fills, protectives placed
            TestBars.Create(15200, 15600, 14500, 15400)); // bar 2: TP hits (high 15600 >= 15500)

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(group);
        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        Assert.Equal(2, result.Fills.Count);
        // PnL: 1 * (15500 - 15000) * 10 * 1 = 5000
        Assert.Equal(5000, group.RealizedPnl);
        Assert.True(group.IsFlat());
    }

    // ── T15: SameBarSlAndTpReachable_SlWins ─────────────────────

    [Fact]
    public void SameBarSlAndTpReachable_SlWins()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? group = null;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                group = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 10m, slPrice: 14000,
                    tpLevels: [new TpLevel { Price = 16000, ClosurePercentage = 1.0m }]);
            }
        })
        { DataSubscriptions = [sub] };

        // Bar 2: both SL and TP are reachable (Low=13900 <= 14000, High=16100 >= 16000)
        // SL is a Stop order (submitted first), TP is a Limit order
        // SL fills first → OnTrade cancels TP → TP skipped by defensive check
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0: order submitted
            TestBars.Create(15000, 15100, 14900, 15000),  // bar 1: entry fills, protectives placed
            TestBars.Create(15000, 16100, 13900, 15000)); // bar 2: both SL+TP reachable

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(group);
        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        Assert.Equal(2, result.Fills.Count); // entry + SL only (TP cancelled)
        Assert.True(group.RealizedPnl < 0); // SL loss
    }

    // ── T16: MultiTp_PartialClose ───────────────────────────────

    [Fact]
    public void MultiTp_PartialClose()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? group = null;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                group = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 10m, slPrice: 14000,
                    tpLevels:
                    [
                        new TpLevel { Price = 15500, ClosurePercentage = 0.5m },
                        new TpLevel { Price = 16000, ClosurePercentage = 0.5m }
                    ]);
            }
        })
        { DataSubscriptions = [sub] };

        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0: order submitted
            TestBars.Create(15000, 15100, 14900, 15000),  // bar 1: entry fills, protectives placed
            TestBars.Create(15200, 15600, 15100, 15400),  // bar 2: TP1 hits (high 15600 >= 15500), close 50%
            TestBars.Create(15400, 16100, 15300, 15900)); // bar 3: TP2 hits (high 16100 >= 16000), close remaining

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(group);
        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        Assert.Equal(3, result.Fills.Count); // entry + TP1 + TP2

        // TP1: 50% of total 10 = 5 at 15500. PnL = 1*(15500-15000)*5*1 = 2500
        // TP2: 50% of total 10 = 5 at 16000. PnL = 1*(16000-15000)*5*1 = 5000
        Assert.Equal(7500, group.RealizedPnl);
        Assert.Equal(0m, group.RemainingQuantity);
    }

    // ── T17: ConcurrentGroups_IndependentLifecycles ─────────────

    [Fact]
    public void ConcurrentGroups_IndependentLifecycles()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? group1 = null, group2 = null;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                // Group 1: Buy, SL=14000, TP=16000
                group1 = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 5m, slPrice: 14000,
                    tpLevels: [new TpLevel { Price = 16000, ClosurePercentage = 1.0m }]);

                // Group 2: Buy, SL=14500, TP=15500
                group2 = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 5m, slPrice: 14500,
                    tpLevels: [new TpLevel { Price = 15500, ClosurePercentage = 1.0m }]);
            }
        })
        { DataSubscriptions = [sub] };

        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0: both orders submitted
            TestBars.Create(15000, 15100, 14900, 15000),  // bar 1: both entries fill
            TestBars.Create(15200, 15600, 15100, 15400),  // bar 2: group2 TP hits (high=15600 >= 15500), group1 TP not hit
            TestBars.Create(15400, 16100, 15300, 15900)); // bar 3: group1 TP hits (high=16100 >= 16000)

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(group1);
        Assert.NotNull(group2);
        Assert.Equal(OrderGroupStatus.Closed, group1.Status);
        Assert.Equal(OrderGroupStatus.Closed, group2.Status);
        Assert.Equal(4, result.Fills.Count); // 2 entries + 2 TPs
    }

    // ── T18: TrailingStop_UpdateSlOnEachBar ──────────────────────

    [Fact]
    public void TrailingStop_UpdateSlOnEachBar()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? group = null;
        long highestClose = 0;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                group = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 10m, slPrice: 14500,
                    tpLevels: [new TpLevel { Price = 20000, ClosurePercentage = 1.0m }]);
            }
            else if (group is { Status: OrderGroupStatus.ProtectionActive })
            {
                // Trail SL: move to close - 500 if price made a new high
                if (bar.Close > highestClose)
                {
                    highestClose = bar.Close;
                    var newSl = bar.Close - 500;
                    if (newSl > group.SlPrice)
                        reg.UpdateStopLoss(group.GroupId, newSl, ctx);
                }
            }
        })
        { DataSubscriptions = [sub] };

        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0: order submitted
            TestBars.Create(15000, 15100, 14900, 15000),  // bar 1: entry fills at 15000, SL at 14500
            TestBars.Create(15200, 15400, 15100, 15300),  // bar 2: trail SL to 14800 (15300-500)
            TestBars.Create(15400, 15800, 15300, 15700),  // bar 3: trail SL to 15200 (15700-500)
            TestBars.Create(15500, 15600, 15100, 15200)); // bar 4: SL hits (low=15100 <= 15200)

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(group);
        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        Assert.Equal(2, result.Fills.Count); // entry + SL

        // SL filled at trailed price 15200, not original 14500
        var slFill = result.Fills[1];
        Assert.Equal(15200L, slFill.Price);
    }

    // ── T19: EventsEmitted_AllTransitions ───────────────────────

    [Fact]
    public void EventsEmitted_AllTransitions()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        var events = new List<OrderGroupEvent>();
        var bus = new CapturingEventBus(events);

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 10m, slPrice: 14000,
                    tpLevels: [new TpLevel { Price = 15500, ClosurePercentage = 1.0m }],
                    tag: "test-tag");
            }
        })
        { DataSubscriptions = [sub] };

        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0
            TestBars.Create(15000, 15100, 14900, 15000),  // bar 1: entry fills
            TestBars.Create(15200, 15600, 15100, 15400)); // bar 2: TP fills

        CreateEngine().Run([bars], strategy, DefaultOptions(), bus: bus);

        // Expected transitions: EntrySubmitted, EntryFilled, SlPlaced, TpPlaced, TpFilled, ProtectiveCancelled (SL)
        var transitions = events.Select(e => e.Transition).ToList();

        Assert.Contains(OrderGroupTransition.EntrySubmitted, transitions);
        Assert.Contains(OrderGroupTransition.EntryFilled, transitions);
        Assert.Contains(OrderGroupTransition.SlPlaced, transitions);
        Assert.Contains(OrderGroupTransition.TpPlaced, transitions);
        Assert.Contains(OrderGroupTransition.TpFilled, transitions);
        Assert.Contains(OrderGroupTransition.ProtectiveCancelled, transitions);
        Assert.All(events, e => Assert.Equal("test-tag", e.Tag));
    }

    // ── T20: MultiTp_SlHit_CancelsBothTps ───────────────────────

    [Fact]
    public void MultiTp_SlHit_CancelsBothTps()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? group = null;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                group = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 10m, slPrice: 14800,
                    tpLevels:
                    [
                        new TpLevel { Price = 15500, ClosurePercentage = 0.5m },
                        new TpLevel { Price = 16000, ClosurePercentage = 0.5m }
                    ]);
            }
        })
        { DataSubscriptions = [sub] };

        // Bar 0: order submitted
        // Bar 1: entry fills at 15000. SL + TP1 + TP2 all placed.
        // Bar 2: SL hits (low=14700 <= 14800). Both TPs cancelled.
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0
            TestBars.Create(15000, 15100, 14900, 15000),  // bar 1: entry fills, all protectives placed
            TestBars.Create(15000, 15100, 14700, 14800)); // bar 2: SL hits

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(group);
        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        Assert.Equal(2, result.Fills.Count); // entry + SL only (both TPs cancelled)
        // PnL: 1 * (14800 - 15000) * 10 * 1 = -2000
        Assert.Equal(-2000, group.RealizedPnl);
    }

    // ── T21: ConcurrentLimitEntries_OneFills_OtherPending ────────

    [Fact]
    public void ConcurrentLimitEntries_OneFills_OtherPending()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? groupA = null, groupB = null;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                // Group A: Buy Limit at 14800, SL=14000, TP=16000
                groupA = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Limit,
                    quantity: 10m, slPrice: 14000,
                    tpLevels: [new TpLevel { Price = 16000, ClosurePercentage = 1.0m }],
                    entryLimitPrice: 14800);

                // Group B: Buy Limit at 14500, SL=13500, TP=16000
                groupB = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Limit,
                    quantity: 10m, slPrice: 13500,
                    tpLevels: [new TpLevel { Price = 16000, ClosurePercentage = 1.0m }],
                    entryLimitPrice: 14500);
            }
        })
        { DataSubscriptions = [sub] };

        // Bar 0: orders submitted (open=15000, above both limits)
        // Bar 1: Low=14700 → A fills (14700 <= 14800), B stays pending (14700 > 14500)
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0: orders submitted
            TestBars.Create(15000, 15100, 14700, 14800),  // bar 1: A fills, B stays pending
            TestBars.Create(14800, 14900, 14600, 14800)); // bar 2: padding (B still pending, low 14600 > limit 14500)

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(groupA);
        Assert.NotNull(groupB);
        Assert.Equal(OrderGroupStatus.ProtectionActive, groupA.Status);
        Assert.Equal(OrderGroupStatus.PendingEntry, groupB.Status);
        Assert.Single(result.Fills); // only A's entry
        Assert.Equal(2, registry.ActiveGroupCount);
    }

    // ── T22: ConcurrentGroups_SlOnOne_TpOnAnother ─────────────────

    [Fact]
    public void ConcurrentGroups_SlOnOne_TpOnAnother()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? groupA = null, groupB = null;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                // Group A: Buy, SL=14500, TP=16000, qty=5
                groupA = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 5m, slPrice: 14500,
                    tpLevels: [new TpLevel { Price = 16000, ClosurePercentage = 1.0m }]);

                // Group B: Buy, SL=14000, TP=15500, qty=5
                groupB = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 5m, slPrice: 14000,
                    tpLevels: [new TpLevel { Price = 15500, ClosurePercentage = 1.0m }]);
            }
        })
        { DataSubscriptions = [sub] };

        // Bar 0: orders submitted
        // Bar 1: both entries fill at 15000
        // Bar 2: Low=14400, High=15600
        //   A's SL at 14500 triggers (14400 <= 14500), A's TP at 16000 doesn't (15600 < 16000)
        //   B's SL at 14000 doesn't trigger (14400 > 14000), B's TP at 15500 triggers (15600 >= 15500)
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0
            TestBars.Create(15000, 15100, 14900, 15000),  // bar 1: both entries fill
            TestBars.Create(15000, 15600, 14400, 15000)); // bar 2: A's SL + B's TP

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(groupA);
        Assert.NotNull(groupB);
        Assert.Equal(OrderGroupStatus.Closed, groupA.Status);
        Assert.Equal(OrderGroupStatus.Closed, groupB.Status);
        Assert.Equal(4, result.Fills.Count); // 2 entries + A's SL + B's TP

        // A: SL loss = 1 * (14500 - 15000) * 5 * 1 = -2500
        Assert.Equal(-2500, groupA.RealizedPnl);
        // B: TP profit = 1 * (15500 - 15000) * 5 * 1 = 2500
        Assert.Equal(2500, groupB.RealizedPnl);
        Assert.True(registry.IsFlat);
    }

    // ── T23: LiquidateGroup_Integration_MarketCloseNextBar ────────

    [Fact]
    public void LiquidateGroup_Integration_MarketCloseNextBar()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? group = null;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                group = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 10m, slPrice: 14000,
                    tpLevels: [new TpLevel { Price = 20000, ClosurePercentage = 1.0m }]);
            }
            else if (i == 2 && group is { Status: OrderGroupStatus.ProtectionActive })
            {
                // Strategy decides to liquidate on bar 2
                reg.LiquidateGroup(group.GroupId, ctx);
            }
        })
        { DataSubscriptions = [sub] };

        // Bar 0: order submitted
        // Bar 1: entry fills at 15000
        // Bar 2: strategy calls LiquidateGroup (market order submitted)
        // Bar 3: market order fills at open=15300
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0
            TestBars.Create(15000, 15100, 14900, 15000),  // bar 1: entry fills
            TestBars.Create(15100, 15300, 15000, 15200),  // bar 2: liquidation submitted
            TestBars.Create(15300, 15400, 15200, 15350)); // bar 3: liquidation fills at open=15300

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(group);
        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        Assert.Equal(2, result.Fills.Count); // entry + liquidation
        // PnL: 1 * (15300 - 15000) * 10 * 1 = 3000
        Assert.Equal(3000, group.RealizedPnl);
        Assert.True(registry.IsFlat);
    }

    // ── T24: LiquidateGroup_ConcurrentGroups_OnlyTargetLiquidated ─

    [Fact]
    public void LiquidateGroup_ConcurrentGroups_OnlyTargetLiquidated()
    {
        var registry = new TradeRegistryModule(new TradeRegistryParams());
        var sub = new DataSubscription(TestAsset, OneMinute);
        OrderGroup? groupA = null, groupB = null;

        var strategy = new TestStrategy(registry, onBar: (bar, sub, ctx, reg, i) =>
        {
            if (i == 0)
            {
                // Group A: Buy, SL=14000, TP=20000 (wide TP — will be liquidated)
                groupA = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 5m, slPrice: 14000,
                    tpLevels: [new TpLevel { Price = 20000, ClosurePercentage = 1.0m }]);

                // Group B: Buy, SL=14000, TP=15500 (will reach TP naturally)
                groupB = reg.OpenGroup(ctx, TestAsset, OrderSide.Buy, OrderType.Market,
                    quantity: 5m, slPrice: 14000,
                    tpLevels: [new TpLevel { Price = 15500, ClosurePercentage = 1.0m }]);
            }
            else if (i == 2 && groupA is { Status: OrderGroupStatus.ProtectionActive })
            {
                // Strategy liquidates A on bar 2
                reg.LiquidateGroup(groupA.GroupId, ctx);
            }
        })
        { DataSubscriptions = [sub] };

        // Bar 0: orders submitted
        // Bar 1: both entries fill at 15000
        // Bar 2: strategy liquidates A (market close submitted)
        // Bar 3: A's liquidation fills at open=15200. B's TP at 15500 reached (high=15600)
        var bars = TestBars.CreateSeries(Start, OneMinute,
            TestBars.Create(15000, 15200, 14900, 15100),  // bar 0
            TestBars.Create(15000, 15100, 14900, 15000),  // bar 1: both fill
            TestBars.Create(15100, 15300, 15000, 15200),  // bar 2: strategy liquidates A
            TestBars.Create(15200, 15600, 15100, 15400)); // bar 3: A liq fills, B TP fills

        var result = CreateEngine().Run([bars], strategy, DefaultOptions());

        Assert.NotNull(groupA);
        Assert.NotNull(groupB);
        Assert.Equal(OrderGroupStatus.Closed, groupA.Status);
        Assert.Equal(OrderGroupStatus.Closed, groupB.Status);
        Assert.Equal(4, result.Fills.Count); // 2 entries + A liq + B TP

        // A: liquidation PnL = 1 * (15200 - 15000) * 5 * 1 = 1000
        Assert.Equal(1000, groupA.RealizedPnl);
        // B: TP PnL = 1 * (15500 - 15000) * 5 * 1 = 2500
        Assert.Equal(2500, groupB.RealizedPnl);
        Assert.True(registry.IsFlat);
    }

    // ── Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// A simple event bus that captures OrderGroupEvent instances for assertion.
    /// </summary>
    private sealed class CapturingEventBus(List<OrderGroupEvent> captured) : IEventBus
    {
        public void Emit<T>(T evt) where T : IBacktestEvent
        {
            if (evt is OrderGroupEvent grpEvt)
                captured.Add(grpEvt);
        }
    }
}

file static class OrderGroupExtensions
{
    public static bool IsFlat(this OrderGroup group) => group.RemainingQuantity <= 0m;
}
