using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public sealed class IbWrapperOrderTests
{
    [Fact]
    public async Task OrderStatus_CompletesAck()
    {
        var w = new IbWrapper();
        var ack = w.RegisterOrderAck(42);
        w.orderStatus(42, "Submitted", 0, 1, 0, 0, 0, 0, 0, "", 0);
        var result = await ack.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal("Submitted", result.Status);
    }

    [Fact]
    public void ExecDetails_DedupsByExecId()
    {
        var w = new IbWrapper();
        var fills = new List<IbFill>();
        w.RegisterOrderSink(_ => { }, fills.Add);
        var exec = IbExecFactory.Make(orderId: 42, execId: "E1", shares: 1, price: 100);
        w.execDetails(1, IbExecFactory.Contract(), exec);
        w.execDetails(1, IbExecFactory.Contract(), exec); // replay (reconnect)
        Assert.Single(fills); // applied once
    }

    [Fact]
    public async Task Error_WithWarningCode_DoesNotFaultAck()
    {
        var w = new IbWrapper();
        var ack = w.RegisterOrderAck(42);
        w.error(42, 0, 399, "order message warning", ""); // 399 = informational
        Assert.False(ack.IsFaulted);
        w.orderStatus(42, "Submitted", 0, 1, 0, 0, 0, 0, 0, "", 0);
        var result = await ack.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal("Submitted", result.Status);
    }

    [Fact]
    public void Error_WithRejectCode_FaultsAck()
    {
        var w = new IbWrapper();
        var ack = w.RegisterOrderAck(42);
        w.error(42, 0, 201, "order rejected", ""); // 201 = rejected
        Assert.True(ack.IsFaulted);
    }
}
