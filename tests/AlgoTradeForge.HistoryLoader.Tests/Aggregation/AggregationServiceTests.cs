using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using AlgoTradeForge.HistoryLoader.WebApi.Aggregation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

public sealed class AggregationServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AggregationJob SmallJob = new(
        JobId: "svc-test-job",
        Source: new DataFeedDescriptor("/data", "binance", "BTCUSDT", "1m", DataFeedKind.TimeBar),
        AssetDir: "/data/binance/BTCUSDT",
        OutcomeFeedId: "EqV_1m_1000",
        TypeCode: "EqV",
        ThresholdAbsolute: 1000m,
        ThresholdScaled: 1000,
        ThresholdUnit: "base_asset",
        ThresholdInputMode: "absolute",
        ThresholdConvenienceInput: null,
        SourceScale: new ScaleContext(0.01m, 0.0001m),
        AccumulatorScale: new ScaleContext(0.01m, 0.0001m),
        MaxPartitionSizeMB: 1,
        ToolVersion: "test-1.0");

    private static AggregationResult SmallResult() => new(
        JobId: "svc-test-job",
        OutcomeFeedId: "EqV_1m_1000",
        BarCount: 2,
        PartitionsWritten: ["2024-01.csv"],
        FirstBarTs: "1704067200000",
        LastBarTs: "1704153600000",
        ActualOvershootPct: 0,
        MaxOvershootPct: 0,
        EstimatedOvershootPct: 0,
        MedianSourceRecordValue: 0,
        NFactor: 0,
        DurationSeconds: 0.1);

    private static IServiceScopeFactory ScopeFactoryFor(IAggregationPipeline pipeline)
    {
        var services = new ServiceCollection();
        services.AddSingleton(pipeline);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    // -------------------------------------------------------------------------
    // 1. Happy path — pipeline emits one Progress event; sink receives it
    //    via the ordered drain and the terminal Complete is called last.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_RoutesPipelineProgress_ToSink_AndCompletes()
    {
        var pipeline = Substitute.For<IAggregationPipeline>();
        pipeline.Run(Arg.Any<AggregationJob>(), Arg.Any<Action<ProgressEvent>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<Action<ProgressEvent>?>()?.Invoke(
                    new ProgressEvent.Progress("svc-test-job", "2024-01", 10, 500));
                return Task.FromResult(SmallResult());
            });

        var svc = new AggregationService(ScopeFactoryFor(pipeline), NullLogger<AggregationService>.Instance);
        var sink = new RecordingSink();
        await svc.Run(new AggregationRunRequest(SmallJob), sink, Ct);

        Assert.True(sink.WasCompleted);
        Assert.NotEmpty(sink.Reports);
    }

    // -------------------------------------------------------------------------
    // 2. Cancellation — pipeline throws OCE on the passed ct; service must route
    //    to sink.Cancel("user_cancelled") and not call Complete.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_Cancel_RoutesCancelToSink_NotComplete()
    {
        using var cts = new CancellationTokenSource();
        var pipeline = Substitute.For<IAggregationPipeline>();
        pipeline.Run(Arg.Any<AggregationJob>(), Arg.Any<Action<ProgressEvent>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                cts.Cancel();
                ci.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult(SmallResult());  // unreachable
            });

        var svc = new AggregationService(ScopeFactoryFor(pipeline), NullLogger<AggregationService>.Instance);
        var sink = new RecordingSink();
        await svc.Run(new AggregationRunRequest(SmallJob), sink, cts.Token);

        Assert.Equal("user_cancelled", sink.CancelReason);
        Assert.False(sink.WasCompleted);
    }

    // -------------------------------------------------------------------------
    // 3. Fault tolerance — if sink.Report throws a non-OCE exception the
    //    consumer absorbs it (best-effort progress) so the terminal Complete
    //    still runs and Run does NOT throw.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Run_ProgressReportThrows_StillReachesTerminal_DoesNotThrow()
    {
        var pipeline = Substitute.For<IAggregationPipeline>();
        pipeline.Run(Arg.Any<AggregationJob>(), Arg.Any<Action<ProgressEvent>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<Action<ProgressEvent>?>()?.Invoke(
                    new ProgressEvent.Progress("svc-test-job", "2024-01", 10, 500));
                return Task.FromResult(SmallResult());
            });

        var svc = new AggregationService(ScopeFactoryFor(pipeline), NullLogger<AggregationService>.Instance);
        var sink = new ThrowingReportSink();

        await svc.Run(new AggregationRunRequest(SmallJob), sink, Ct);  // must not throw

        Assert.True(sink.WasCompleted);
    }

    // Sink whose Report always throws to exercise the consumer's fault-absorption path.
    private sealed class ThrowingReportSink : IJobProgressSink
    {
        public bool WasCompleted { get; private set; }

        public Task Report(string progressJson, CancellationToken ct = default) =>
            throw new InvalidOperationException("sqlite busy");

        public Task Started(string startedPayloadJson, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task Complete(string resultPayloadJson, CancellationToken ct = default)
        {
            WasCompleted = true;
            return Task.CompletedTask;
        }

        public Task Fail(string code, string message, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task Cancel(string reason, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
