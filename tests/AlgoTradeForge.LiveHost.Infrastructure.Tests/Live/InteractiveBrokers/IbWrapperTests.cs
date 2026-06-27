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

    [Fact]
    public void TickByTickAllLast_RoutesToRegisteredTickSink()
    {
        var w = new IbWrapper();
        IbTradeUpdate? seen = null;
        w.RegisterTickSink(20, u => seen = u);

        w.tickByTickAllLast(20, tickType: 1, time: 1_700_000_000L, price: 296.98, size: 3m,
            tickAttribLast: new IBApi.TickAttribLast(), exchange: "NASDAQ", specialConditions: "");

        Assert.NotNull(seen);
        Assert.Equal(1_700_000_000L, seen!.Value.TimeSec);
        Assert.Equal(296.98, seen.Value.Price);
        Assert.Equal(3m, seen.Value.Size);
    }

    [Fact]
    public void RealtimeBar_RoutesToRegisteredBarSink()
    {
        var w = new IbWrapper();
        IbRealtimeBar? seen = null;
        w.RegisterBarSink(21, b => seen = b);

        w.realtimeBar(21, time: 1_700_000_005L, open: 1.0, high: 2.0, low: 0.5, close: 1.5,
            volume: 10m, WAP: 1.2m, count: 4);

        Assert.NotNull(seen);
        Assert.Equal(1_700_000_005L, seen!.Value.DateSec);
        Assert.Equal(2.0, seen.Value.High);
        Assert.Equal(10m, seen.Value.Volume);
    }

    [Fact]
    public void ReleaseMarketData_StopsRouting()
    {
        var w = new IbWrapper();
        int calls = 0;
        w.RegisterTickSink(22, _ => calls++);
        w.ReleaseMarketData(22);
        w.tickByTickAllLast(22, 1, 1L, 1.0, 1m, new IBApi.TickAttribLast(), "", "");
        Assert.Equal(0, calls);
    }

    [Fact]
    public void UnknownReqId_IsIgnored_NoThrow()
    {
        var w = new IbWrapper();
        w.tickByTickAllLast(999, 1, 1L, 1.0, 1m, new IBApi.TickAttribLast(), "", "");
        w.realtimeBar(999, 1L, 1, 1, 1, 1, 1m, 1m, 1);
    }

    [Fact]
    public void ConnectionClosed_RaisesConnectionDropped()
    {
        var w = new IbWrapper();
        int drops = 0;
        w.ConnectionDropped += () => drops++;
        w.connectionClosed();
        Assert.Equal(1, drops);
    }

    [Fact]
    public void Error1101_RaisesConnectionDropped_But1100And1102AndOtherNoticesDoNot()
    {
        var w = new IbWrapper();
        int drops = 0;
        w.ConnectionDropped += () => drops++;
        w.error(-1, 0L, 2104, "Market data farm connection is OK", "");            // benign data-farm notice
        w.error(-1, 0L, 1100, "Connectivity between IB and TWS has been lost.", ""); // soft loss — self-heals, no reconnect
        w.error(-1, 0L, 1102, "Connectivity restored, data maintained.", "");       // restored, data kept — no action
        Assert.Equal(0, drops);

        // 1101 = restored but DATA LOST → subscriptions must be re-issued → signal recovery.
        w.error(-1, 0L, 1101, "Connectivity restored, data lost.", "");
        Assert.Equal(1, drops);
    }

    [Fact]
    public async Task Error_OnHistoricalReqId_FaultsHistoricalAwaiter()
    {
        var w = new IbWrapper();
        var task = w.RegisterHistoricalTicks(50);

        // A reqHistoricalTicks error (e.g. 10189 no market-data permission) must fault the awaiter, not be
        // dropped — otherwise FetchTrades blocks to its 30s timeout and wedges the bar source's recovery.
        w.error(50, 0L, 10189, "No market data permissions for the requested security.", "");

        var ex = await Assert.ThrowsAsync<IbRequestException>(async () => await task);
        Assert.Equal(10189, ex.ErrorCode);
    }

    [Fact]
    public async Task ResetForReconnect_RearmsNextValidId()
    {
        var w = new IbWrapper();
        w.nextValidId(1);
        Assert.Equal(1, await w.NextValidId.WaitAsync(TestContext.Current.CancellationToken));
        w.ResetForReconnect();
        w.nextValidId(7);
        Assert.Equal(7, await w.NextValidId.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HistoricalTicksLast_AccumulatesAndCompletesOnDone()
    {
        var w = new IbWrapper();
        var task = w.RegisterHistoricalTicks(30);
        w.historicalTicksLast(30, new[] { HistTick(1700, 10.0, 2m) }, done: false);
        w.historicalTicksLast(30, new[] { HistTick(1701, 11.0, 3m) }, done: true);

        var result = await task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.Equal(1700, result[0].TimeSec);
        Assert.Equal(11.0, result[1].Price);
    }

    private static IBApi.HistoricalTickLast HistTick(long time, double price, decimal size) =>
        new(time, new IBApi.TickAttribLast(), price, size, "", "");
}
