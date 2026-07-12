using AlgoTradeForge.HistoryLoader.Application.Jobs;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Jobs;

public sealed class JobEventSignalTests
{
    [Fact]
    public async Task Signal_WakesReaderCapturedBeforeSignal()
    {
        var sig = new JobEventSignal();
        var next = sig.Next("j1");                 // capture BEFORE signal
        Assert.False(next.IsCompleted);
        sig.Signal("j1");
        await next.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);   // completes
        Assert.True(next.IsCompleted);
    }

    [Fact]
    public async Task ManyReaders_AllWakeOnOneSignal()
    {
        var sig = new JobEventSignal();
        var readers = Enumerable.Range(0, 8).Select(_ => sig.Next("j2")).ToArray();
        sig.Signal("j2");
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);   // all complete
    }

    [Fact]
    public void Signal_WithoutCell_IsNoOp_LaterNextIsIncomplete()
    {
        var sig = new JobEventSignal();
        sig.Signal("j3");                          // no cell exists → no-op, no parked completed TCS
        var next = sig.Next("j3");
        Assert.False(next.IsCompleted);            // must NOT be already-completed → no busy-spin
    }
}
