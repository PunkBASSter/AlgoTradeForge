using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Live;

public class OrderGroupReconcilerTests
{
    private static readonly CryptoAsset TestAsset = CryptoAsset.Create("BTCUSDT", "Binance",
        decimalDigits: 2,
        minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    private static readonly TpLevel[] TwoTps =
    [
        new TpLevel { Price = 15500, ClosurePercentage = 0.5m },
        new TpLevel { Price = 16000, ClosurePercentage = 0.5m }
    ];

    private static TradeRegistryModule CreateModule()
    {
        var module = new TradeRegistryModule(new TradeRegistryParams());
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
        new(orderId, TestAsset, DateTimeOffset.UtcNow, price, quantity, side, 0L);

    private static Order MakeOrder(long orderId) =>
        new()
        {
            Id = orderId,
            Asset = TestAsset,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 10m,
        };

    private static OrderGroup SetupActiveGroup(TradeRegistryModule module, IOrderContext ctx)
    {
        var group = module.OpenGroup(
            ctx, TestAsset, OrderSide.Buy, OrderType.Market,
            quantity: 10m, slPrice: 14000, tpLevels: TwoTps)!;

        module.OnFill(
            MakeFill(group.EntryOrderId, 15000, 10m, OrderSide.Buy),
            MakeOrder(group.EntryOrderId), ctx);

        return group;
    }

    // ── R1: NoExpectedOrders_DoesNothing ──────────────────────

    [Fact]
    public async Task NoExpectedOrders_DoesNothing()
    {
        var client = Substitute.For<IExchangeOrderClient>();
        var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);
        var module = CreateModule();
        var ctx = MockOrderContext();

        await reconciler.ReconcileAsync("BTCUSDT", module, id => id, ctx, CancellationToken.None);

        // No groups → no exchange queries
        await client.DidNotReceive().GetOpenOrdersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── R2: AllOrdersPresent_NoRepairs ────────────────────────

    [Fact]
    public async Task AllOrdersPresent_NoRepairs()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();
        var group = SetupActiveGroup(module, ctx);

        // Simulate all orders present on exchange with positive IDs
        var exchangeOrders = new List<ExchangeOpenOrder>
        {
            new(100, "BTCUSDT", "SELL", "STOP_LOSS", 10m, 0m, 14000m, "NEW"),
            new(101, "BTCUSDT", "SELL", "LIMIT", 5m, 15500m, 0m, "NEW"),
            new(102, "BTCUSDT", "SELL", "LIMIT", 5m, 16000m, 0m, "NEW"),
        };

        var client = Substitute.For<IExchangeOrderClient>();
        client.GetOpenOrdersAsync("BTCUSDT", Arg.Any<CancellationToken>())
            .Returns(exchangeOrders);

        var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);

        // Map local (negative) IDs to exchange (positive) IDs
        var expected = module.GetExpectedOrders();
        var idMap = new Dictionary<long, long>();
        var exchangeIdx = 0;
        foreach (var e in expected)
            idMap[e.OrderId] = exchangeOrders[exchangeIdx++].OrderId;

        var submitCountBefore = ctx.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Submit");

        await reconciler.ReconcileAsync("BTCUSDT", module, id => idMap.GetValueOrDefault(id, id), ctx, CancellationToken.None);

        // No new submits (no repairs needed)
        var submitCountAfter = ctx.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Submit");
        Assert.Equal(submitCountBefore, submitCountAfter);

        // No cancels of orphans
        await client.DidNotReceive().CancelOrderAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ── R3: MissingSl_RepairsViaModule ────────────────────────

    [Fact]
    public async Task MissingSl_RepairsViaModule()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();
        var group = SetupActiveGroup(module, ctx);

        var expected = module.GetExpectedOrders();
        var slOrder = expected.First(e => e.Type == ExpectedOrderType.StopLoss);
        var tp1Order = expected.First(e => e.Type == ExpectedOrderType.TakeProfit);
        var tp2Order = expected.Last(e => e.Type == ExpectedOrderType.TakeProfit);

        // Exchange only has the 2 TPs — SL is missing
        var exchangeOrders = new List<ExchangeOpenOrder>
        {
            new(201, "BTCUSDT", "SELL", "LIMIT", 5m, 15500m, 0m, "NEW"),
            new(202, "BTCUSDT", "SELL", "LIMIT", 5m, 16000m, 0m, "NEW"),
        };

        var client = Substitute.For<IExchangeOrderClient>();
        client.GetOpenOrdersAsync("BTCUSDT", Arg.Any<CancellationToken>())
            .Returns(exchangeOrders);

        var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);

        var idMap = new Dictionary<long, long>
        {
            [slOrder.OrderId] = 200,   // maps to missing exchange ID
            [tp1Order.OrderId] = 201,
            [tp2Order.OrderId] = 202,
        };

        var oldSlId = group.SlOrderId;

        await reconciler.ReconcileAsync("BTCUSDT", module, id => idMap.GetValueOrDefault(id, id), ctx, CancellationToken.None);

        // SL should have been repaired — new SL ID
        Assert.NotEqual(oldSlId, group.SlOrderId);
        Assert.NotEqual(0, group.SlOrderId);
    }

    // ── R4: MissingTp_RepairsViaModule ────────────────────────

    [Fact]
    public async Task MissingTp_RepairsViaModule()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();
        var group = SetupActiveGroup(module, ctx);

        var expected = module.GetExpectedOrders();
        var slOrder = expected.First(e => e.Type == ExpectedOrderType.StopLoss);
        var tp1Order = expected.First(e => e.Type == ExpectedOrderType.TakeProfit);
        var tp2Order = expected.Last(e => e.Type == ExpectedOrderType.TakeProfit);

        // Exchange has SL + TP1 — TP2 is missing
        var exchangeOrders = new List<ExchangeOpenOrder>
        {
            new(300, "BTCUSDT", "SELL", "STOP_LOSS", 10m, 0m, 14000m, "NEW"),
            new(301, "BTCUSDT", "SELL", "LIMIT", 5m, 15500m, 0m, "NEW"),
        };

        var client = Substitute.For<IExchangeOrderClient>();
        client.GetOpenOrdersAsync("BTCUSDT", Arg.Any<CancellationToken>())
            .Returns(exchangeOrders);

        var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);

        var idMap = new Dictionary<long, long>
        {
            [slOrder.OrderId] = 300,
            [tp1Order.OrderId] = 301,
            [tp2Order.OrderId] = 302,  // maps to missing exchange ID
        };

        var oldTp2Id = group.TpLevels[1].OrderId;

        await reconciler.ReconcileAsync("BTCUSDT", module, id => idMap.GetValueOrDefault(id, id), ctx, CancellationToken.None);

        // TP2 should have been repaired — new order ID
        Assert.NotEqual(oldTp2Id, group.TpLevels[1].OrderId);
        Assert.NotEqual(0, group.TpLevels[1].OrderId);
    }

    // ── R5: OrphanedOrder_CancelledOnExchange ─────────────────

    [Fact]
    public async Task OrphanedOrder_CancelledOnExchange()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();
        var group = SetupActiveGroup(module, ctx);

        var expected = module.GetExpectedOrders();
        var slOrder = expected.First(e => e.Type == ExpectedOrderType.StopLoss);
        var tp1Order = expected.First(e => e.Type == ExpectedOrderType.TakeProfit);
        var tp2Order = expected.Last(e => e.Type == ExpectedOrderType.TakeProfit);

        // Exchange has all expected orders PLUS an orphan
        var exchangeOrders = new List<ExchangeOpenOrder>
        {
            new(400, "BTCUSDT", "SELL", "STOP_LOSS", 10m, 0m, 14000m, "NEW"),
            new(401, "BTCUSDT", "SELL", "LIMIT", 5m, 15500m, 0m, "NEW"),
            new(402, "BTCUSDT", "SELL", "LIMIT", 5m, 16000m, 0m, "NEW"),
            new(999, "BTCUSDT", "BUY", "LIMIT", 1m, 10000m, 0m, "NEW"), // orphan
        };

        var client = Substitute.For<IExchangeOrderClient>();
        client.GetOpenOrdersAsync("BTCUSDT", Arg.Any<CancellationToken>())
            .Returns(exchangeOrders);

        var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);

        var idMap = new Dictionary<long, long>
        {
            [slOrder.OrderId] = 400,
            [tp1Order.OrderId] = 401,
            [tp2Order.OrderId] = 402,
        };

        await reconciler.ReconcileAsync("BTCUSDT", module, id => idMap.GetValueOrDefault(id, id), ctx, CancellationToken.None);

        // Orphaned order 999 should be cancelled on exchange
        await client.Received(1).CancelOrderAsync("BTCUSDT", 999, Arg.Any<CancellationToken>());
    }

    // ── R6: TwoActiveGroups_MissingOrderInEach_BothRepaired ────

    [Fact]
    public async Task TwoActiveGroups_MissingOrderInEach_BothRepaired()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();

        // Setup two active groups
        var group1 = SetupActiveGroup(module, ctx);
        var group2 = SetupActiveGroup(module, ctx);

        var expected = module.GetExpectedOrders();
        // group1: SL + 2 TPs = 3 expected; group2: SL + 2 TPs = 3 expected; total = 6
        Assert.Equal(6, expected.Count);

        var g1Sl = expected.First(e => e.GroupId == group1.GroupId && e.Type == ExpectedOrderType.StopLoss);
        var g1Tp1 = expected.First(e => e.GroupId == group1.GroupId && e.Type == ExpectedOrderType.TakeProfit);
        var g1Tp2 = expected.Last(e => e.GroupId == group1.GroupId && e.Type == ExpectedOrderType.TakeProfit);
        var g2Sl = expected.First(e => e.GroupId == group2.GroupId && e.Type == ExpectedOrderType.StopLoss);
        var g2Tp1 = expected.First(e => e.GroupId == group2.GroupId && e.Type == ExpectedOrderType.TakeProfit);
        var g2Tp2 = expected.Last(e => e.GroupId == group2.GroupId && e.Type == ExpectedOrderType.TakeProfit);

        // Exchange: group1 missing SL (500), group2 missing TP2 (505)
        var exchangeOrders = new List<ExchangeOpenOrder>
        {
            // group1: TP1 + TP2 present, SL missing
            new(501, "BTCUSDT", "SELL", "LIMIT", 5m, 15500m, 0m, "NEW"),
            new(502, "BTCUSDT", "SELL", "LIMIT", 5m, 16000m, 0m, "NEW"),
            // group2: SL + TP1 present, TP2 missing
            new(503, "BTCUSDT", "SELL", "STOP_LOSS", 10m, 0m, 14000m, "NEW"),
            new(504, "BTCUSDT", "SELL", "LIMIT", 5m, 15500m, 0m, "NEW"),
        };

        var client = Substitute.For<IExchangeOrderClient>();
        client.GetOpenOrdersAsync("BTCUSDT", Arg.Any<CancellationToken>())
            .Returns(exchangeOrders);

        var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);

        var idMap = new Dictionary<long, long>
        {
            [g1Sl.OrderId] = 500,  // missing
            [g1Tp1.OrderId] = 501,
            [g1Tp2.OrderId] = 502,
            [g2Sl.OrderId] = 503,
            [g2Tp1.OrderId] = 504,
            [g2Tp2.OrderId] = 505, // missing
        };

        var oldGroup1SlId = group1.SlOrderId;
        var oldGroup2Tp2Id = group2.TpLevels[1].OrderId;

        await reconciler.ReconcileAsync("BTCUSDT", module, id => idMap.GetValueOrDefault(id, id), ctx, CancellationToken.None);

        // Group1 SL should be repaired
        Assert.NotEqual(oldGroup1SlId, group1.SlOrderId);
        Assert.NotEqual(0, group1.SlOrderId);

        // Group2 TP2 should be repaired
        Assert.NotEqual(oldGroup2Tp2Id, group2.TpLevels[1].OrderId);
        Assert.NotEqual(0, group2.TpLevels[1].OrderId);
    }

    // ── R7: OrphanInKnownPendingIds_NotCancelled ───────────────

    [Fact]
    public async Task OrphanInKnownPendingIds_NotCancelled()
    {
        var module = CreateModule();
        var ctx = MockOrderContext();
        var group = SetupActiveGroup(module, ctx);

        var expected = module.GetExpectedOrders();
        var slOrder = expected.First(e => e.Type == ExpectedOrderType.StopLoss);
        var tp1Order = expected.First(e => e.Type == ExpectedOrderType.TakeProfit);
        var tp2Order = expected.Last(e => e.Type == ExpectedOrderType.TakeProfit);

        // Exchange has all expected + an order from a non-TradeRegistry strategy
        var exchangeOrders = new List<ExchangeOpenOrder>
        {
            new(600, "BTCUSDT", "SELL", "STOP_LOSS", 10m, 0m, 14000m, "NEW"),
            new(601, "BTCUSDT", "SELL", "LIMIT", 5m, 15500m, 0m, "NEW"),
            new(602, "BTCUSDT", "SELL", "LIMIT", 5m, 16000m, 0m, "NEW"),
            new(999, "BTCUSDT", "BUY", "LIMIT", 1m, 10000m, 0m, "NEW"), // non-TradeRegistry order
        };

        var client = Substitute.For<IExchangeOrderClient>();
        client.GetOpenOrdersAsync("BTCUSDT", Arg.Any<CancellationToken>())
            .Returns(exchangeOrders);

        var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);

        var idMap = new Dictionary<long, long>
        {
            [slOrder.OrderId] = 600,
            [tp1Order.OrderId] = 601,
            [tp2Order.OrderId] = 602,
        };

        // 999 IS in knownPendingIds — should NOT be cancelled
        var knownPendingIds = new HashSet<long> { 999 };

        var result = await reconciler.DetectAsync(
            "BTCUSDT", expected, id => idMap.GetValueOrDefault(id, id), knownPendingIds, CancellationToken.None);

        // No orphans because 999 is in knownPendingIds
        Assert.Empty(result.OrphanIds);
        Assert.Empty(result.MissingByGroup);
    }
}
