using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation.Jobs;

/// <summary>P1b-17 — bounded queue capacity + depth tracking.</summary>
public sealed class AggregationJobQueueTests
{
    private static AggregationJob NewJob(string id) =>
        new(
            JobId: id,
            Source: new DataFeedDescriptor("/", "binance", "BTCUSDT", "1m", DataFeedKind.TimeBar),
            AssetDir: "/binance/BTCUSDT",
            OutcomeFeedId: $"EqV_1m_{id}",
            TypeCode: "EqV",
            ThresholdAbsolute: 1000m,
            ThresholdScaled: 1000,
            ThresholdUnit: "base_asset",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: new ScaleContext(0.01m, 0.0001m),
            AccumulatorScale: new ScaleContext(0.01m, 0.0001m),
            MaxPartitionSizeMB: 100,
            ToolVersion: "test");

    private static AggregationJobQueue Build(int capacity) =>
        new(Options.Create(new HistoryLoaderOptions
        {
            Aggregator = new AggregatorOptions { MaxQueueDepth = capacity }
        }));

    [Fact]
    public void TryWrite_BelowCapacity_Accepts()
    {
        var queue = Build(capacity: 3);
        Assert.True(queue.TryWrite(NewJob("a")));
        Assert.True(queue.TryWrite(NewJob("b")));
        Assert.Equal(2, queue.CurrentDepth);
    }

    [Fact]
    public void TryWrite_AtCapacity_ReturnsFalse()
    {
        var queue = Build(capacity: 2);
        Assert.True(queue.TryWrite(NewJob("a")));
        Assert.True(queue.TryWrite(NewJob("b")));
        Assert.False(queue.TryWrite(NewJob("c")));
        Assert.Equal(2, queue.CurrentDepth);
    }

    [Fact]
    public async Task Reader_DequeueDecrementsDepth()
    {
        var queue = Build(capacity: 3);
        queue.TryWrite(NewJob("a"));
        queue.TryWrite(NewJob("b"));

        var dequeued = await queue.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("a", dequeued.JobId);
        Assert.Equal(1, queue.CurrentDepth);
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(capacity: 0));
    }
}
