using AlgoTradeForge.Application.Backtests;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Backtests;

public sealed class ProgressTrackingEventBusSinkTests
{
    [Fact]
    public void ProcessedBars_Initially_Zero()
    {
        var sink = new ProgressTrackingEventBusSink();

        Assert.Equal(0, sink.ProcessedBars);
    }

    [Fact]
    public void Write_Increments_ProcessedBars()
    {
        var sink = new ProgressTrackingEventBusSink();
        var data = new byte[] { 0x7B, 0x7D }; // "{}"

        sink.Write(data);
        sink.Write(data);
        sink.Write(data);

        Assert.Equal(3, sink.ProcessedBars);
    }

    [Fact]
    public void ProcessedBars_Is_ThreadSafe()
    {
        var sink = new ProgressTrackingEventBusSink();
        var data = new byte[] { 0x7B, 0x7D };
        const int iterations = 10_000;

        Parallel.For(0, iterations, _ => sink.Write(data));

        Assert.Equal(iterations, sink.ProcessedBars);
    }
}
