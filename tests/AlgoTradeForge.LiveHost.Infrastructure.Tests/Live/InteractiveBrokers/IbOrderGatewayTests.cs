using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public sealed class IbOrderGatewayTests
{
    [Fact]
    public async Task Place_AllocatesId_Places_AwaitsAck_ReturnsId()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 100);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        await using var gw = GatewayFixture.Build(client, wrapper, reports.Add);

        var placeTask = gw.Place("DU1", GatewayFixture.Aapl, GatewayFixture.MktBuy(1), ct);
        client.SignalAck(wrapper, "Submitted"); // fake fires orderStatus on the wrapper
        var id = await placeTask;

        Assert.Equal(100, id);
        Assert.Equal(100, client.LastPlacedOrderId);
    }

    [Fact]
    public async Task Place_OnRejectError_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 100);
        var wrapper = new IbWrapper();
        await using var gw = GatewayFixture.Build(client, wrapper, _ => { });
        var placeTask = gw.Place("DU1", GatewayFixture.Aapl, GatewayFixture.MktBuy(1), ct);
        wrapper.error(100, 0, 201, "rejected", "");
        await Assert.ThrowsAsync<IbRequestException>(() => placeTask);
    }

    [Fact]
    public async Task ExecDetails_EmitsExecutionReport_OffPump_WithAssetAndSide()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 100);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        await using var gw = GatewayFixture.Build(client, wrapper, reports.Add);
        var placeTask = gw.Place("DU1", GatewayFixture.Aapl, GatewayFixture.MktBuy(1), ct);
        client.SignalAck(wrapper, "Submitted");
        await placeTask;

        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(100, "E1", 1, 100));
        await GatewayFixture.WaitForReport(reports, ct);

        Assert.Single(reports);
        Assert.Equal(ExecType.Trade, reports[0].ExecType);
        Assert.Equal(OrderSide.Buy, reports[0].Side);
        Assert.Equal(0m, reports[0].Commission); // gross at emit
    }

    // Asserts all 11 ExecutionReport fields are correctly mapped for a Sell/Limit order with a known fill.
    // The stored side is authoritative; the IB fill string ("SLD") happens to agree here, so this verifies
    // correct field plumbing. For the mismatched-string case see ExecDetails_StoredSideWins_WhenFillStringDisagrees.
    [Fact]
    public async Task ExecDetails_MapsAllElevenFields_SellLimitOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 200);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        await using var gw = GatewayFixture.Build(client, wrapper, reports.Add);

        var request = GatewayFixture.LmtSell(qty: 7m, lmtPrice: 195.50);
        var placeTask = gw.Place("DU1", GatewayFixture.AaplAsset, GatewayFixture.Aapl, request,
            OrderSide.Sell, OrderType.Limit, originalQuantity: 7m, ct);
        client.SignalAck(wrapper, "Submitted"); // any status completes the placement ack TCS
        var orderId = await placeTask;

        const long fillTimeUnixSec = 1_750_000_000L;
        // A single fill for the full 7 → cumulative reaches OriginalQuantity → terminal Filled.
        wrapper.execDetails(1, IbExecFactory.Contract(),
            IbExecFactory.Make(200, "EXEC-42", shares: 7m, price: 194.75, side: "SLD",
                time: fillTimeUnixSec.ToString()));
        await GatewayFixture.WaitForReport(reports, ct);

        Assert.Single(reports);
        var r = reports[0];
        Assert.Equal(orderId,                                              r.OrderId);
        Assert.Equal(OrderSide.Sell,                                       r.Side);
        Assert.Equal(OrderType.Limit,                                      r.Type);
        Assert.Equal(7m,                                                   r.OriginalQuantity);
        Assert.Equal(194.75m,                                              r.LastFillPrice);
        Assert.Equal(7m,                                                   r.LastFillQty);
        Assert.Equal(0m,                                                   r.Commission);
        Assert.Equal(ExecType.Trade,                                       r.ExecType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(fillTimeUnixSec),  r.TransactionTime);
        Assert.Equal(GatewayFixture.AaplAsset.Name,                        r.Symbol);
        Assert.Equal(OrderStatus.Filled,                                   r.Status);
    }

    // Stored order side wins even when the IB fill string disagrees. Place a Sell; fire execDetails with
    // side "BOT" (wrong string); the report must carry OrderSide.Sell (the intent we placed).
    [Fact]
    public async Task ExecDetails_StoredSideWins_WhenFillStringDisagrees()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 400);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        await using var gw = GatewayFixture.Build(client, wrapper, reports.Add);

        var request = GatewayFixture.LmtSell(qty: 5m, lmtPrice: 200.00);
        var placeTask = gw.Place("DU1", GatewayFixture.AaplAsset, GatewayFixture.Aapl, request,
            OrderSide.Sell, OrderType.Limit, originalQuantity: 5m, ct);
        client.SignalAck(wrapper, "Submitted");
        var orderId = await placeTask;

        // Deliberately wrong fill string ("BOT" instead of "SLD") — stored side must prevail.
        wrapper.execDetails(1, IbExecFactory.Contract(),
            IbExecFactory.Make((int)orderId, "EXEC-MISMATCH", shares: 5m, price: 199.00, side: "BOT"));
        await GatewayFixture.WaitForReport(reports, ct);

        Assert.Single(reports);
        Assert.Equal(OrderSide.Sell, reports[0].Side);
    }

    // #5(a): a partial fill seen with no terminal signal must NOT prematurely terminate the order.
    [Fact]
    public async Task ExecDetails_PartialFill_MapsPartiallyFilled_AndKeepsOrderTracked()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 100);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        await using var gw = GatewayFixture.Build(client, wrapper, reports.Add);

        var request = GatewayFixture.LmtSell(qty: 3m, lmtPrice: 100.0);
        var placeTask = gw.Place("DU1", GatewayFixture.AaplAsset, GatewayFixture.Aapl, request,
            OrderSide.Sell, OrderType.Limit, originalQuantity: 3m, ct);
        client.SignalAck(wrapper, "Submitted");
        await placeTask;

        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(100, "E1", shares: 1m, price: 100));
        await GatewayFixture.WaitForReport(reports, ct);

        Assert.Equal(OrderStatus.PartiallyFilled, reports[0].Status);
        Assert.Equal(1, gw.TrackedOrderCount); // still tracked — not prematurely terminated
    }

    // #5(b): partial then final fill → exactly two reports, the last terminal Filled; order then pruned (#8).
    [Fact]
    public async Task ExecDetails_PartialThenFinalFill_EmitsFilledOnce_AndPrunes()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 100);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        await using var gw = GatewayFixture.Build(client, wrapper, reports.Add);

        var request = GatewayFixture.LmtSell(qty: 3m, lmtPrice: 100.0);
        var placeTask = gw.Place("DU1", GatewayFixture.AaplAsset, GatewayFixture.Aapl, request,
            OrderSide.Sell, OrderType.Limit, originalQuantity: 3m, ct);
        client.SignalAck(wrapper, "Submitted");
        await placeTask;

        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(100, "E1", shares: 1m, price: 100));
        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(100, "E2", shares: 2m, price: 100));
        await GatewayFixture.WaitForReportCount(reports, 2, ct);

        Assert.Equal(2, reports.Count);
        Assert.Equal(OrderStatus.PartiallyFilled, reports[0].Status);
        Assert.Equal(OrderStatus.Filled, reports[1].Status);
        Assert.Equal(0, gw.TrackedOrderCount); // pruned after terminal fill (#8)
    }

    // #5(c): a "Filled" orderStatus arriving AFTER the last execDetails must not emit a second terminal report —
    // the final fill already carried Filled (cumulative-driven), independent of orderStatus timing.
    [Fact]
    public async Task FilledOrderStatusAfterFinalFill_DoesNotDoubleEmit()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 100);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        await using var gw = GatewayFixture.Build(client, wrapper, reports.Add);

        var placeTask = gw.Place("DU1", GatewayFixture.Aapl, GatewayFixture.MktBuy(1), ct);
        client.SignalAck(wrapper, "Submitted");
        await placeTask;

        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(100, "E1", shares: 1m, price: 100));
        await GatewayFixture.WaitForReport(reports, ct);
        wrapper.orderStatus(100, "Filled", 1, 0, 0, 0, 0, 0, 0, "", 0); // late terminal status

        await Task.Delay(50, ct); // give any erroneous second emit a chance to surface

        Assert.Single(reports);
        Assert.Equal(OrderStatus.Filled, reports[0].Status);
    }

    // #5 cancel path + #8 prune: a partially-filled order that is cancelled emits the partial fill then a
    // Canceled termination, and the per-order maps are freed.
    [Fact]
    public async Task CancelStatus_AfterPartialFill_EmitsCanceled_AndPrunes()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 100);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        await using var gw = GatewayFixture.Build(client, wrapper, reports.Add);

        var request = GatewayFixture.LmtSell(qty: 5m, lmtPrice: 100.0);
        var placeTask = gw.Place("DU1", GatewayFixture.AaplAsset, GatewayFixture.Aapl, request,
            OrderSide.Sell, OrderType.Limit, originalQuantity: 5m, ct);
        client.SignalAck(wrapper, "Submitted");
        await placeTask;

        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(100, "E1", shares: 2m, price: 100));
        wrapper.orderStatus(100, "Cancelled", 2, 3, 0, 0, 0, 0, 0, "", 0);
        await GatewayFixture.WaitForReportCount(reports, 2, ct);

        Assert.Equal(2, reports.Count);
        Assert.Equal(OrderStatus.PartiallyFilled, reports[0].Status);
        Assert.Equal(ExecType.Canceled, reports[1].ExecType);
        Assert.Equal(0, gw.TrackedOrderCount); // pruned after cancel (#8)
    }

    // SnapshotOpenOrders arms the wrapper's accumulator, requests open orders, and returns the broker pushback
    // grouped by account (the reconnect reconciliation source).
    [Fact]
    public async Task SnapshotOpenOrders_RequestsAndGroupsPushbackByAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 500);
        var wrapper = new IbWrapper();
        await using var gw = GatewayFixture.Build(client, wrapper, _ => { });

        var snapTask = gw.SnapshotOpenOrders(ct);

        // Simulate IB's pushback: two accounts, three open orders. Fire on the (test) thread after arming.
        await Poll(() => client.OpenOrdersRequested > 0);
        wrapper.openOrder(1001, IbExecFactory.Contract("AAPL"), OpenOrder("DU1", "SELL", "STP"), OrderStateOf("Submitted"));
        wrapper.openOrder(2002, IbExecFactory.Contract("MSFT"), OpenOrder("DU2", "SELL", "LMT"), OrderStateOf("Submitted"));
        wrapper.openOrder(3003, IbExecFactory.Contract("AAPL"), OpenOrder("DU1", "BUY", "LMT"), OrderStateOf("Submitted"));
        wrapper.openOrderEnd();

        var byAccount = await snapTask;

        Assert.Equal(1, client.OpenOrdersRequested);
        Assert.Equal([1001L, 3003L], byAccount["DU1"].OrderBy(x => x));
        Assert.Equal([2002L], byAccount["DU2"]);
    }

    // Shutdown safety-net: cancel-all for one account cancels exactly that account's resting orders.
    [Fact]
    public async Task CancelAllOpenOrders_CancelsOnlyTheAccountsRestingOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 700);
        var wrapper = new IbWrapper();
        await using var gw = GatewayFixture.Build(client, wrapper, _ => { });

        var cancelTask = gw.CancelAllOpenOrders("DU1", ct);

        await Poll(() => client.OpenOrdersRequested > 0);
        wrapper.openOrder(1001, IbExecFactory.Contract("AAPL"), OpenOrder("DU1", "SELL", "STP"), OrderStateOf("Submitted"));
        wrapper.openOrder(2002, IbExecFactory.Contract("MSFT"), OpenOrder("DU2", "SELL", "LMT"), OrderStateOf("Submitted"));
        wrapper.openOrder(3003, IbExecFactory.Contract("AAPL"), OpenOrder("DU1", "BUY", "LMT"), OrderStateOf("Submitted"));
        wrapper.openOrderEnd();

        await cancelTask;

        Assert.Equal([1001, 3003], client.Cancelled.OrderBy(x => x)); // DU2's 2002 left untouched
    }

    // Lane saturation drops the fill AND counts it. DropWrite makes Writer.TryWrite always return true,
    // so the only signal is the itemDropped callback (DroppedFills) — the old TryWrite-return check was dead.
    [Fact]
    public async Task LaneSaturation_DropsFill_AndCountsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 400);
        var wrapper = new IbWrapper();

        using var release = new ManualResetEventSlim(false);
        using var firstReportSeen = new ManualResetEventSlim(false);
        // onReport blocks the single worker on the first fill, so the cap-1 lane saturates on later fills.
        await using var gw = new IbOrderGateway(client, wrapper,
            _ => { firstReportSeen.Set(); release.Wait(ct); },
            NullLogger<IbOrderGateway>.Instance, laneCapacity: 1);

        var placeTask = gw.Place("DU1", GatewayFixture.Aapl, GatewayFixture.MktBuy(1), ct);
        client.SignalAck(wrapper, "Submitted");
        await placeTask;

        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(400, "E1", 1, 100)); // drained → worker blocks
        firstReportSeen.Wait(ct);
        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(400, "E2", 1, 100)); // fills the slot
        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(400, "E3", 1, 100)); // dropped
        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(400, "E4", 1, 100)); // dropped

        await Poll(() => gw.DroppedFills > 0);
        Assert.True(gw.DroppedFills > 0);

        release.Set(); // unblock the worker so DisposeAsync can drain and exit
    }

    private static IBApi.Order OpenOrder(string account, string action, string orderType) =>
        new() { Account = account, Action = action, OrderType = orderType, TotalQuantity = 1m };

    private static IBApi.OrderState OrderStateOf(string status) => new() { Status = status };

    private static async Task Poll(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++)
            await Task.Delay(5);
    }

    // Verifies DisposeAsync drains fills already written to the lane before exiting the worker.
    [Fact]
    public async Task DisposeAsync_DrainsQueuedFills_BeforeExiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 300);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        var gw = GatewayFixture.Build(client, wrapper, reports.Add);

        var placeTask = gw.Place("DU1", GatewayFixture.Aapl, GatewayFixture.MktBuy(1), ct);
        client.SignalAck(wrapper, "Submitted");
        await placeTask;

        // Write a fill onto the lane, then immediately dispose — the worker must drain it before stopping.
        wrapper.execDetails(1, IbExecFactory.Contract(), IbExecFactory.Make(300, "DRAIN-1", 1, 100));
        await gw.DisposeAsync();

        // No further async wait needed: DisposeAsync completes only after the worker exits.
        Assert.Single(reports);
        Assert.Equal(ExecType.Trade, reports[0].ExecType);
    }
}
