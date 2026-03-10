using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.TradeRegistry;

public class TradeRegistryModuleTests
{
    private static readonly Asset DefaultAsset = TestAssets.Aapl;

    private static readonly TpLevel[] SingleTp =
    [
        new TpLevel { Price = 16000, ClosurePercentage = 1.0m }
    ];

    private static readonly TpLevel[] TwoTps =
    [
        new TpLevel { Price = 15500, ClosurePercentage = 0.5m },
        new TpLevel { Price = 16000, ClosurePercentage = 0.5m }
    ];

    private static TradeRegistryModule CreateModule(int maxConcurrentGroups = 0)
    {
        var module = new TradeRegistryModule(new TradeRegistryParams
        {
            MaxConcurrentGroups = maxConcurrentGroups
        });
        module.SetEventBus(NullEventBus.Instance);
        return module;
    }

    private static IOrderContext MockOrderContext()
    {
        var ctx = Substitute.For<IOrderContext>();
        ctx.Submit(Arg.Any<Order>()).Returns(ci => ci.ArgAt<Order>(0).Id);
        return ctx;
    }

    private static Fill MakeFill(long orderId, long price, decimal quantity, OrderSide side) =>
        new(orderId, DefaultAsset, DateTimeOffset.UtcNow, price, quantity, side, 0L);

    private static Order MakeOrder(long orderId) =>
        new()
        {
            Id = orderId,
            Asset = DefaultAsset,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 10m,
        };

    // ── T1: OpenGroup_SubmitsEntryOrder ──────────────────────────

    [Fact]
    public void OpenGroup_SubmitsEntryOrder()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: SingleTp);

        Assert.NotNull(group);
        Assert.Equal(OrderGroupStatus.PendingEntry, group.Status);
        Assert.Equal(10m, group.EntryQuantity);
        Assert.Equal(14000, group.SlPrice);
        ctx.Received(1).Submit(Arg.Any<Order>());
    }

    // ── T2: OpenGroup_AtMaxCapacity_ReturnsNull ─────────────────

    [Fact]
    public void OpenGroup_AtMaxCapacity_ReturnsNull()
    {
        var module = CreateModule(maxConcurrentGroups: 1);
        var ctx = MockOrderContext();

        var group1 = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: SingleTp);

        var group2 = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: SingleTp);

        Assert.NotNull(group1);
        Assert.Null(group2);
    }

    // ── T3: OnFill_EntryFill_TransitionsToProtectionActive ──────

    [Fact]
    public void OnFill_EntryFill_TransitionsToProtectionActive()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: SingleTp)!;

        // Simulate entry fill
        var fill = MakeFill(group.EntryOrderId, price: 15000, quantity: 10m, OrderSide.Buy);
        var order = MakeOrder(group.EntryOrderId);
        module.OnFill(fill, order, ctx);

        Assert.Equal(OrderGroupStatus.ProtectionActive, group.Status);
        Assert.Equal(15000, group.EntryPrice);
        // 1 entry + 1 SL + 1 TP = 3 total submits
        ctx.Received(3).Submit(Arg.Any<Order>());
    }

    // ── T4: OnFill_SlFill_ClosesGroupAndCancelsTp ───────────────

    [Fact]
    public void OnFill_SlFill_ClosesGroupAndCancelsTp()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: SingleTp)!;

        // Entry fill
        var entryFill = MakeFill(group.EntryOrderId, price: 15000, quantity: 10m, OrderSide.Buy);
        module.OnFill(entryFill, MakeOrder(group.EntryOrderId), ctx);

        // SL fill (price dropped to SL)
        var slFill = MakeFill(group.SlOrderId, price: 14000, quantity: 10m, OrderSide.Sell);
        module.OnFill(slFill, MakeOrder(group.SlOrderId), ctx);

        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        Assert.Equal(0m, group.RemainingQuantity);
        // PnL: direction=1 * (14000-15000) * 10 * 1 = -10000
        Assert.Equal(-10000, group.RealizedPnl);
        // TP should be cancelled
        ctx.Received(1).Cancel(Arg.Any<long>());
    }

    // ── T5: OnFill_TpFill_ReducesQuantity ───────────────────────

    [Fact]
    public void OnFill_TpFill_ReducesQuantity()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: TwoTps)!;

        // Entry fill
        var entryFill = MakeFill(group.EntryOrderId, price: 15000, quantity: 10m, OrderSide.Buy);
        module.OnFill(entryFill, MakeOrder(group.EntryOrderId), ctx);

        // TP1 fill: closes 50% of total = 5 units
        var tp1OrderId = group.TpLevels[0].OrderId;
        var tp1Fill = MakeFill(tp1OrderId, price: 15500, quantity: 5m, OrderSide.Sell);
        module.OnFill(tp1Fill, MakeOrder(tp1OrderId), ctx);

        Assert.Equal(OrderGroupStatus.ProtectionActive, group.Status);
        Assert.Equal(5m, group.RemainingQuantity);
        Assert.Equal(1, group.FilledTpCount);
        // PnL from TP1: 1 * (15500 - 15000) * 5 * 1 = 2500
        Assert.Equal(2500, group.RealizedPnl);
    }

    // ── T5b: OnFill_TpFill_ReplacesSlWithReducedQuantity ────────

    [Fact]
    public void OnFill_TpFill_ReplacesSlWithReducedQuantity()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();
        var submittedOrders = new List<Order>();
        ctx.Submit(Arg.Any<Order>()).Returns(ci =>
        {
            var o = ci.ArgAt<Order>(0);
            submittedOrders.Add(o);
            return o.Id;
        });

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: TwoTps)!;

        // Entry fill
        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        // TP1 fill: closes 50% = 5 units
        var tp1OrderId = group.TpLevels[0].OrderId;
        module.OnFill(
            MakeFill(tp1OrderId, 15500, 5m, OrderSide.Sell),
            MakeOrder(tp1OrderId), ctx);

        // The LAST submitted Stop order should have reduced qty
        var lastSl = submittedOrders.Last(o => o.Type == OrderType.Stop);
        Assert.Equal(5m, lastSl.Quantity);
        Assert.Equal(14000, lastSl.StopPrice);
    }

    // ── T6: OnFill_FinalTpFill_CancelsSlAndCloses ───────────────

    [Fact]
    public void OnFill_FinalTpFill_CancelsSlAndCloses()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: TwoTps)!;

        // Entry fill
        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        // TP1 fill
        var tp1OrderId = group.TpLevels[0].OrderId;
        module.OnFill(
            MakeFill(tp1OrderId, 15500, 5m, OrderSide.Sell),
            MakeOrder(tp1OrderId), ctx);

        // TP2 fill (remaining 5 units)
        var tp2OrderId = group.TpLevels[1].OrderId;
        module.OnFill(
            MakeFill(tp2OrderId, 16000, 5m, OrderSide.Sell),
            MakeOrder(tp2OrderId), ctx);

        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        Assert.Equal(0m, group.RemainingQuantity);
        // PnL: TP1 = 2500 + TP2 = 1*(16000-15000)*5*1 = 5000 → total 7500
        Assert.Equal(7500, group.RealizedPnl);
    }

    // ── T7: CancelGroup_PendingEntry ────────────────────────────

    [Fact]
    public void CancelGroup_PendingEntry()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: SingleTp)!;

        var result = module.CancelGroup(group.GroupId, ctx);

        Assert.True(result);
        Assert.Equal(OrderGroupStatus.Cancelled, group.Status);
        ctx.Received(1).Cancel(group.EntryOrderId);
    }

    // ── T8: CancelGroup_ProtectionActive ────────────────────────

    [Fact]
    public void CancelGroup_ProtectionActive()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: SingleTp)!;

        // Entry fill to move to ProtectionActive
        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        var slId = group.SlOrderId;
        var tpId = group.TpLevels[0].OrderId;

        var result = module.CancelGroup(group.GroupId, ctx);

        Assert.True(result);
        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        ctx.Received(1).Cancel(slId);
        ctx.Received(1).Cancel(tpId);
    }

    // ── T9: UpdateStopLoss_ReplacesSLOrder ──────────────────────

    [Fact]
    public void UpdateStopLoss_ReplacesSLOrder()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: SingleTp)!;

        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        var oldSlId = group.SlOrderId;

        var result = module.UpdateStopLoss(group.GroupId, 14500, ctx);

        Assert.True(result);
        Assert.Equal(14500, group.SlPrice);
        Assert.NotEqual(oldSlId, group.SlOrderId);
        ctx.Received(1).Cancel(oldSlId);
        // 1 entry + 1 SL + 1 TP (initial) + 1 new SL = 4 submits
        ctx.Received(4).Submit(Arg.Any<Order>());
    }

    // ── T10: OnFill_UnknownOrderId_NoOp ─────────────────────────

    [Fact]
    public void OnFill_UnknownOrderId_NoOp()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        // Create a group to have some state
        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: SingleTp)!;

        // Fill with an unknown order ID — should be a no-op
        var fill = MakeFill(orderId: 999999, price: 15000, quantity: 10m, OrderSide.Buy);
        var order = MakeOrder(999999);

        module.OnFill(fill, order, ctx);

        Assert.Equal(OrderGroupStatus.PendingEntry, group.Status);
    }

    // ── T11: OpenGroup_ShortSide_CorrectProtectiveDirections ────

    [Fact]
    public void OpenGroup_ShortSide_CorrectProtectiveDirections()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();
        var submittedOrders = new List<Order>();
        ctx.Submit(Arg.Any<Order>()).Returns(ci =>
        {
            var o = ci.ArgAt<Order>(0);
            submittedOrders.Add(o);
            return o.Id;
        });

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Sell, OrderType.Market,
            quantity: 10m, slPrice: 16000,
            tpLevels: [new TpLevel { Price = 14000, ClosurePercentage = 1.0m }])!;

        // Entry fill
        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Sell),
            MakeOrder(group.EntryOrderId), ctx);

        // SL should be Buy Stop, TP should be Buy Limit
        var slOrder = submittedOrders.First(o => o.Type == OrderType.Stop);
        var tpOrder = submittedOrders.First(o => o.Type == OrderType.Limit);

        Assert.Equal(OrderSide.Buy, slOrder.Side);
        Assert.Equal(16000, slOrder.StopPrice);
        Assert.Equal(OrderSide.Buy, tpOrder.Side);
        Assert.Equal(14000, tpOrder.LimitPrice);
    }

    // ── T12: MaxConcurrentGroups_Zero_Unlimited ─────────────────

    [Fact]
    public void MaxConcurrentGroups_Zero_Unlimited()
    {
        var module = CreateModule(maxConcurrentGroups: 0);
        var ctx = MockOrderContext();

        var groups = new List<OrderGroup>();
        for (var i = 0; i < 50; i++)
        {
            var g = module.OpenGroup(
                ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
                quantity: 1m, slPrice: 14000, tpLevels: SingleTp);
            Assert.NotNull(g);
            groups.Add(g);
        }

        Assert.Equal(50, module.ActiveGroupCount);
        Assert.False(module.IsFlat);
    }

    // ── T13a: OnFill_SlFill_CancelsAllPendingTps ──────────────

    [Fact]
    public void OnFill_SlFill_CancelsAllPendingTps()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: TwoTps)!;

        // Entry fill → SL + 2 TPs placed
        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        var tp1Id = group.TpLevels[0].OrderId;
        var tp2Id = group.TpLevels[1].OrderId;
        Assert.NotEqual(0, tp1Id);
        Assert.NotEqual(0, tp2Id);

        // SL fill — should cancel BOTH TPs
        module.OnFill(
            MakeFill(group.SlOrderId, 14000, 10m, OrderSide.Sell),
            MakeOrder(group.SlOrderId), ctx);

        Assert.Equal(OrderGroupStatus.Closed, group.Status);
        ctx.Received(1).Cancel(tp1Id);
        ctx.Received(1).Cancel(tp2Id);
    }

    // ── T13b: OnFill_EntryFill_SubmitsAllTpsUpfront ───────────

    [Fact]
    public void OnFill_EntryFill_SubmitsAllTpsUpfront()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: TwoTps)!;

        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        // Both TPs should have non-zero OrderIds
        Assert.NotEqual(0, group.TpLevels[0].OrderId);
        Assert.NotEqual(0, group.TpLevels[1].OrderId);
        Assert.NotEqual(group.TpLevels[0].OrderId, group.TpLevels[1].OrderId);

        // 1 entry + 1 SL + 2 TPs = 4 total submits
        ctx.Received(4).Submit(Arg.Any<Order>());
    }

    // ── T13c: GetExpectedOrders_ReturnsCorrectSet ─────────────

    [Fact]
    public void GetExpectedOrders_ReturnsCorrectSet()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: TwoTps)!;

        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        var expected = module.GetExpectedOrders();

        // 1 SL + 2 TPs = 3 expected orders
        Assert.Equal(3, expected.Count);
        Assert.Single(expected.Where(e => e.Type == ExpectedOrderType.StopLoss));
        Assert.Equal(2, expected.Count(e => e.Type == ExpectedOrderType.TakeProfit));

        var sl = expected.First(e => e.Type == ExpectedOrderType.StopLoss);
        Assert.Equal(group.SlOrderId, sl.OrderId);
        Assert.Equal(group.GroupId, sl.GroupId);
        Assert.Equal(14000, sl.Price);
        Assert.Equal(10m, sl.Quantity);
    }

    // ── T13d: RepairGroup_ResubmitsMissingSl ──────────────────

    [Fact]
    public void RepairGroup_ResubmitsMissingSl()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: SingleTp)!;

        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        var oldSlId = group.SlOrderId;

        // Repair: SL is "missing" on exchange
        module.RepairGroup(group.GroupId, new HashSet<long> { oldSlId }, ctx);

        // New SL should have been submitted with a different ID
        Assert.NotEqual(oldSlId, group.SlOrderId);
        Assert.NotEqual(0, group.SlOrderId);
        // 1 entry + 1 SL + 1 TP (initial) + 1 new SL (repair) = 4 submits
        ctx.Received(4).Submit(Arg.Any<Order>());
    }

    // ── T13e: RepairGroup_ResubmitsMissingTp ──────────────────

    [Fact]
    public void RepairGroup_ResubmitsMissingTp()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: TwoTps)!;

        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        var oldTp2Id = group.TpLevels[1].OrderId;

        // Repair: TP2 is "missing" on exchange
        module.RepairGroup(group.GroupId, new HashSet<long> { oldTp2Id }, ctx);

        // New TP2 should have been submitted with a different ID
        Assert.NotEqual(oldTp2Id, group.TpLevels[1].OrderId);
        Assert.NotEqual(0, group.TpLevels[1].OrderId);
        // 1 entry + 1 SL + 2 TPs (initial) + 1 new TP (repair) = 5 submits
        ctx.Received(5).Submit(Arg.Any<Order>());
    }

    // ── T13f: RepairGroup_ResubmitsBothMissingSlAndTp ──────────

    [Fact]
    public void RepairGroup_ResubmitsBothMissingSlAndTp()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: TwoTps)!;

        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        var oldSlId = group.SlOrderId;
        var oldTp2Id = group.TpLevels[1].OrderId;

        // Repair: both SL and TP2 are "missing" on exchange
        module.RepairGroup(group.GroupId, new HashSet<long> { oldSlId, oldTp2Id }, ctx);

        // Both should get new IDs
        Assert.NotEqual(oldSlId, group.SlOrderId);
        Assert.NotEqual(0, group.SlOrderId);
        Assert.NotEqual(oldTp2Id, group.TpLevels[1].OrderId);
        Assert.NotEqual(0, group.TpLevels[1].OrderId);
        // 1 entry + 1 SL + 2 TPs (initial) + 1 new SL + 1 new TP (repair) = 6 submits
        ctx.Received(6).Submit(Arg.Any<Order>());
    }

    // ── T13g: OpenGroup_ClosurePercentageExceeds100_ReturnsNull ─

    [Fact]
    public void OpenGroup_ClosurePercentageExceeds100_ReturnsNull()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        TpLevel[] badTps =
        [
            new TpLevel { Price = 15500, ClosurePercentage = 0.6m },
            new TpLevel { Price = 16000, ClosurePercentage = 0.6m } // sum = 1.2
        ];

        var group = module.OpenGroup(
            ctx, DefaultAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: badTps);

        Assert.Null(group);
        ctx.DidNotReceive().Submit(Arg.Any<Order>());
    }
}
