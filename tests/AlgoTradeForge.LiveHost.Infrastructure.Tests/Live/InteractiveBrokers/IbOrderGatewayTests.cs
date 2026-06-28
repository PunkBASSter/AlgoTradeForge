using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
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
        client.SignalAck(wrapper, "Filled"); // "Filled" acks the TCS and records Status → Filled in _latestStatus
        var orderId = await placeTask;

        const long fillTimeUnixSec = 1_750_000_000L;
        wrapper.execDetails(1, IbExecFactory.Contract(),
            IbExecFactory.Make(200, "EXEC-42", shares: 3m, price: 194.75, side: "SLD",
                time: fillTimeUnixSec.ToString()));
        await GatewayFixture.WaitForReport(reports, ct);

        Assert.Single(reports);
        var r = reports[0];
        Assert.Equal(orderId,                                              r.OrderId);
        Assert.Equal(OrderSide.Sell,                                       r.Side);
        Assert.Equal(OrderType.Limit,                                      r.Type);
        Assert.Equal(7m,                                                   r.OriginalQuantity);
        Assert.Equal(194.75m,                                              r.LastFillPrice);
        Assert.Equal(3m,                                                   r.LastFillQty);
        Assert.Equal(0m,                                                   r.Commission);
        Assert.Equal(ExecType.Trade,                                       r.ExecType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(fillTimeUnixSec),  r.TransactionTime);
        Assert.Equal(GatewayFixture.AaplAsset,                             r.Asset);
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
