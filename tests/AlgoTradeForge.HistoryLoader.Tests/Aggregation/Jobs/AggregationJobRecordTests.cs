using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation.Jobs;

/// <summary>
/// Reviewer Issue 1 — pins the capture-before-drain SSE notification contract: a signal captured
/// before <see cref="AggregationJobRecord.AppendEvent"/> / <see cref="AggregationJobRecord.MarkTerminal"/>
/// MUST already be completed by the time the appender returns. Without this guarantee the SSE
/// handler can drop terminal events when the append happens between drain and await.
/// </summary>
public sealed class AggregationJobRecordTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static AggregationJobRecord NewRecord()
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var job = new AggregationJob(
            JobId: "j1",
            Source: new DataFeedDescriptor("/", "binance", "BTCUSDT", "1m", DataFeedKind.TimeBar),
            AssetDir: "/binance/BTCUSDT",
            OutcomeFeedId: "EqV_1m_1000",
            TypeCode: "EqV",
            ThresholdAbsolute: 1000m,
            ThresholdScaled: 1000,
            ThresholdUnit: "base_asset",
            ThresholdInputMode: "absolute",
            ThresholdConvenienceInput: null,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: 100,
            ToolVersion: "test");
        return new AggregationJobRecord { Job = job, QueuedAt = T0 };
    }

    [Fact]
    public void NextEventSignal_CapturedBeforeAppend_IsCompletedAfterAppend()
    {
        // Capture-before-drain pattern relies on this contract: a signal task obtained before
        // an append must be completed (synchronously) by the time AppendEvent returns. The SSE
        // handler captures the signal, then drains, then awaits the captured signal — if any
        // append happened in between, the captured signal is already done so the await returns
        // immediately and the next drain catches the new event.
        var record = NewRecord();
        var captured = record.NextEventSignal;

        record.AppendEvent(new ProgressEvent.Started("j1", "EqV_1m_1000", T0, "1m"));

        Assert.True(captured.IsCompleted);
    }

    [Fact]
    public void NextEventSignal_CapturedBeforeMarkTerminal_IsCompletedAfterMarkTerminal()
    {
        // Same contract for the terminal-transition path — MarkTerminal goes through a single
        // event append + signal swap, so a captured signal must fire.
        var record = NewRecord();
        var captured = record.NextEventSignal;

        var fakeResult = new AggregationResult(
            JobId: "j1", OutcomeFeedId: "EqV_1m_1000",
            BarCount: 0, PartitionsWritten: [],
            FirstBarTs: null, LastBarTs: null,
            ActualOvershootPct: 0d, MaxOvershootPct: 0d,
            EstimatedOvershootPct: 0d, MedianSourceRecordValue: 0d,
            NFactor: 0d, DurationSeconds: 0d);

        record.MarkTerminal(
            state: AggregationJobState.Complete,
            completedAt: T0,
            result: fakeResult,
            error: null,
            barsEmitted: 0,
            terminalEvent: new ProgressEvent.Complete(fakeResult));

        Assert.True(captured.IsCompleted);
    }

    [Fact]
    public void NextEventSignal_AfterAppend_ReturnsDistinctTask()
    {
        // Each append swaps in a fresh TCS — re-reading NextEventSignal after an append must
        // return a different (uncompleted) task so subsequent waits don't fire on stale events.
        var record = NewRecord();

        var beforeFirst = record.NextEventSignal;
        record.AppendEvent(new ProgressEvent.Started("j1", "EqV_1m_1000", T0, "1m"));
        var afterFirst = record.NextEventSignal;

        Assert.NotSame(beforeFirst, afterFirst);
        Assert.False(afterFirst.IsCompleted);
    }
}
