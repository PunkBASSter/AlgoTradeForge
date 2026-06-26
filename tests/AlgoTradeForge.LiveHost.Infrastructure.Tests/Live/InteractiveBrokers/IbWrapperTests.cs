using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using IBApi;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbWrapperTests
{
    private static ContractDetails Details(int conId, string localSymbol, string expiry = "") =>
        new() { Contract = new Contract { ConId = conId, LocalSymbol = localSymbol, LastTradeDateOrContractMonth = expiry } };

    [Fact]
    public async Task ContractDetailsEnd_CompletesWithAllAccumulated()
    {
        var w = new IbWrapper();
        using var request = w.RegisterContractDetails(1);

        w.contractDetails(1, Details(1, "GCZ6", "20261229"));
        w.contractDetails(1, Details(2, "GCG7", "20270226"));
        w.contractDetailsEnd(1);

        var results = await request.Completion;
        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].ConId);
        Assert.Equal("20261229", results[0].LastTradeDate);
        Assert.Equal(2, results[1].ConId);
    }

    [Fact]
    public async Task SingleStk_CompletesWithOne()
    {
        var w = new IbWrapper();
        using var request = w.RegisterContractDetails(3);

        w.contractDetails(3, Details(265598, "AAPL"));
        w.contractDetailsEnd(3);

        var results = await request.Completion;
        var only = Assert.Single(results);
        Assert.Equal(265598, only.ConId);
        Assert.Equal("AAPL", only.LocalSymbol);
    }

    [Fact]
    public async Task Error_OnRequestId_FaultsAwaiter()
    {
        var w = new IbWrapper();
        using var request = w.RegisterContractDetails(7);

        w.error(7, 0L, 200, "No security definition has been found", "");

        var ex = await Assert.ThrowsAsync<IbRequestException>(async () => await request.Completion);
        Assert.Equal(200, ex.ErrorCode);
    }

    [Fact]
    public void Error_ConnectivityNotice_IgnoresMinusOne()
    {
        var w = new IbWrapper();
        // id == -1 is a data-farm/connectivity notice, must not fault any awaiter.
        w.error(-1, 0L, 2104, "Market data farm connection is OK", "");
    }

    [Fact]
    public async Task NextValidId_CompletesTask()
    {
        var w = new IbWrapper();
        w.nextValidId(42);
        Assert.Equal(42, await w.NextValidId);
    }

    [Fact]
    public void DisposingRequest_DropsPending_SoReqIdDoesNotLeak()
    {
        var w = new IbWrapper();
        var first = w.RegisterContractDetails(9);
        w.contractDetailsEnd(9);
        Assert.True(first.Completion.IsCompleted);

        first.Dispose();

        // After the scope's Dispose the reqId is gone: re-registering yields a new, still-pending task rather
        // than the completed one — proving the Pending entry was evicted instead of accumulating for the
        // connection's life.
        using var second = w.RegisterContractDetails(9);
        Assert.NotSame(first.Completion, second.Completion);
        Assert.False(second.Completion.IsCompleted);
    }
}
