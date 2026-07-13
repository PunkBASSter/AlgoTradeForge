using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using AlgoTradeForge.HistoryLoader.WebApi.Aggregation;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Endpoints;

/// <summary>
/// End-to-end shape agreement: each REAL job progress producer emits a progress_json that, once
/// round-tripped through <see cref="JobEnvelope.From"/> (the exact deserialization the GET/list
/// endpoints use), exposes the fields the frontend JobCard reads — done/total for the bar and the
/// per-kind detail fields. This closes the cross-task gap where no test spanned
/// producer → JobEnvelope → FE-read. Non-vacuous: the pre-fix producer shapes (load month-in-phase
/// with no detail, aggregation camelCase, materialize flat-not-nested) all leave Detail null, so
/// every detail assertion here throws against them.
/// </summary>
public sealed class ProgressShapeRoundTripTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IndexJobRow RowWith(string kind, string progressJson) =>
        new(Id: "job-1", Kind: kind, State: "running", ProgressJson: progressJson, Error: null,
            FeedKey: "binance|BTCUSDT|feed|1m", CancelRequested: false, TouchedJson: "{}", RequestJson: null);

    // ---- load --------------------------------------------------------------

    [Fact]
    public async Task Load_Producer_RoundTrips_ToFeReadFields()
    {
        var orchestrator = Substitute.For<IBackfillOrchestrator>();
        orchestrator.TryRunSingle(
                Arg.Any<CollectionAsset>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(),
                Arg.Any<IProgress<ArchiveProgress>?>(), Arg.Any<Func<string, CancellationToken, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<IProgress<ArchiveProgress>?>()?.Report(new ArchiveProgress(3, 12, "2024-03"));
                return Task.FromResult(true);
            });
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = "/data/test" });

        var svc = new ArchiveLoadService(orchestrator, options, Substitute.For<IHistoryIndex>(),
            NullLogger<ArchiveLoadService>.Instance);
        var sink = new RecordingSink();
        await svc.Run(
            new ArchiveLoadRequest(CollectionAssets.Perp("BTCUSDT"), FeedNames.Candles, "1h",
                new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31)),
            sink, Ct);

        var env = JobEnvelope.From(RowWith("load", Assert.Single(sink.Reports)));
        Assert.Equal(3, env.Progress!.Done);
        Assert.Equal(12, env.Progress.Total);
        Assert.Equal("2024-03", env.Progress.Detail!.Value.GetProperty("current_month").GetString());
    }

    // ---- aggregation -------------------------------------------------------

    [Fact]
    public async Task Aggregation_Producer_RoundTrips_ToFeReadFields()
    {
        var pipeline = Substitute.For<IAggregationPipeline>();
        pipeline.Run(Arg.Any<AggregationJob>(), Arg.Any<Action<ProgressEvent>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<Action<ProgressEvent>?>()?.Invoke(
                    new ProgressEvent.Progress("job-1", "2024-01", 42, 500));
                return Task.FromResult(SmallResult());
            });

        var services = new ServiceCollection();
        services.AddSingleton(pipeline);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var svc = new AggregationService(scopeFactory, NullLogger<AggregationService>.Instance);
        var sink = new RecordingSink();
        await svc.Run(new AggregationRunRequest(SmallJob), sink, Ct);

        var env = JobEnvelope.From(RowWith("aggregation", Assert.Single(sink.Reports)));
        var detail = env.Progress!.Detail!.Value;
        Assert.Equal("2024-01", detail.GetProperty("current_partition").GetString());
        Assert.Equal(42, detail.GetProperty("bars_emitted").GetInt32());
    }

    // ---- materialize -------------------------------------------------------

    [Fact]
    public async Task Materialize_Producer_RoundTrips_ToFeReadFields()
    {
        var baseSink = new RecordingSink();
        var stageSink = new MaterializeProgressSink(baseSink, stageIndex: 1, stagesTotal: 2, phase: "aggregate");
        await stageSink.Report("""{"inner":true}""", Ct);

        var env = JobEnvelope.From(RowWith("materialize", Assert.Single(baseSink.Reports)));
        Assert.Equal(1, env.Progress!.Done);
        Assert.Equal(2, env.Progress.Total);
        var detail = env.Progress.Detail!.Value;
        Assert.Equal(1, detail.GetProperty("stage_index").GetInt32());
        Assert.Equal(2, detail.GetProperty("stages_total").GetInt32());
        Assert.True(detail.TryGetProperty("stage", out _));
    }

    // ---- shared fixtures ---------------------------------------------------

    private static readonly AggregationJob SmallJob = new(
        JobId: "job-1",
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
        JobId: "job-1",
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
}
