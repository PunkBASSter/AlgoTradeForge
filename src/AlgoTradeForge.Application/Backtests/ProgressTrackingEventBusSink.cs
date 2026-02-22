using AlgoTradeForge.Application.Events;

namespace AlgoTradeForge.Application.Backtests;

public sealed class ProgressTrackingEventBusSink : ISink
{
    private long _processedBars;

    public long ProcessedBars => Interlocked.Read(ref _processedBars);

    public void Write(ReadOnlyMemory<byte> utf8Json)
    {
        Interlocked.Increment(ref _processedBars);
    }
}
