using System.Threading.Channels;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class SessionMarketDataChannelTests
{
    [Fact]
    public void Market_data_channel_drops_newest_and_counts_without_blocking()
    {
        long dropped = 0;
        var data = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropNewest, SingleReader = true },
            itemDropped: _ => Interlocked.Increment(ref dropped));

        for (var i = 0; i < 10; i++)
            Assert.True(data.Writer.TryWrite(() => { }), "DropNewest TryWrite must never return false");

        Assert.True(Interlocked.Read(ref dropped) >= 8,
            $"expected at least 8 drops, got {Interlocked.Read(ref dropped)}");
    }

    [Fact]
    public async Task Drain_runs_exec_before_data_and_terminates_when_both_writers_complete()
    {
        var exec = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var data = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.DropNewest });

        var order = new List<string>();
        // Interleave: enqueue a data item, then an exec item, before the drain starts.
        Assert.True(data.Writer.TryWrite(() => order.Add("data1")));
        Assert.True(exec.Writer.TryWrite(() => order.Add("exec1")));
        Assert.True(data.Writer.TryWrite(() => order.Add("data2")));
        Assert.True(exec.Writer.TryWrite(() => order.Add("exec2")));

        exec.Writer.TryComplete();
        data.Writer.TryComplete();

        await LiveSessionDispatcher.DrainSessionQueues(
            exec.Reader, data.Reader, NullLogger.Instance, Guid.NewGuid(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // All four ran, and exec items preceded data items (exec drained to empty first).
        Assert.Equal(4, order.Count);
        Assert.True(order.IndexOf("exec1") < order.IndexOf("data1"),
            $"exec must run before data; order=[{string.Join(",", order)}]");
        Assert.True(order.IndexOf("exec2") < order.IndexOf("data1"),
            $"all available exec must run before data; order=[{string.Join(",", order)}]");
    }

    [Fact]
    public async Task Drain_terminates_cleanly_on_cancellation()
    {
        var exec = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var data = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.DropNewest });

        using var cts = new CancellationTokenSource();
        var drain = LiveSessionDispatcher.DrainSessionQueues(
            exec.Reader, data.Reader, NullLogger.Instance, Guid.NewGuid(), cts.Token);

        cts.Cancel();
        // Must complete (no throw escaping) and not hang.
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(drain.IsCompletedSuccessfully);
    }
}
