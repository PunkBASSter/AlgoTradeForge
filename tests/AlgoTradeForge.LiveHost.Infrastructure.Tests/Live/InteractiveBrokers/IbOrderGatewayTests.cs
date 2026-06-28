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
}
