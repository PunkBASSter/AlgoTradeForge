using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Endpoints;

/// <summary>
/// Verifies normalized error shapes ({code,message}) across endpoint groups,
/// the F2 symbol_blocked code in PostLoad, and the unified-envelope alias for GET /loads/{id}.
/// </summary>
public sealed class ErrorNormalizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IOptionsMonitor<HistoryLoaderOptions> Options(string dataRoot = "/test-root")
    {
        var m = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        m.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = dataRoot });
        return m;
    }

    // -----------------------------------------------------------------------
    // F2 — PostLoad: symbol_blocked vs symbol_not_declared
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PostLoad_DeclaredButBlockedAsset_Returns_SymbolBlocked()
    {
        var holder = new CollectionPlanHolder();
        holder.Publish(new CollectionPlan(
            Assets: [],
            Blocked: [new BlockedAsset("binance", "BTC/USDT", "BTCUSDT", "precision unknown")],
            Warnings: []));

        var result = await LoadEndpoints.PostLoad(
            body: new LoadRequest("binance", "BTCUSDT", AssetTypes.Spot,
                FeedNames.Candles, "1h", new DateOnly(2024, 1, 1), new DateOnly(2024, 3, 31)),
            options: Options(),
            registry: new ArchiveMaterializerRegistry([]),
            index: Substitute.For<IHistoryIndex>(),
            wakeup: Substitute.For<IJobWakeupQueue>(),
            planSource: holder,
            ct: Ct);

        var body = Assert.IsType<JsonHttpResult<ErrorBody>>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, body.StatusCode);
        Assert.Equal("symbol_blocked", body.Value!.Code);
    }

    [Fact]
    public async Task PostLoad_UndeclaredAndNotBlocked_Returns_SymbolNotDeclared()
    {
        var result = await LoadEndpoints.PostLoad(
            body: new LoadRequest("binance", "BTCUSDT", AssetTypes.Spot,
                FeedNames.Candles, "1h", new DateOnly(2024, 1, 1), new DateOnly(2024, 3, 31)),
            options: Options(),
            registry: new ArchiveMaterializerRegistry([]),
            index: Substitute.For<IHistoryIndex>(),
            wakeup: Substitute.For<IJobWakeupQueue>(),
            planSource: new CollectionPlanHolder(),
            ct: Ct);

        var body = Assert.IsType<JsonHttpResult<ErrorBody>>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, body.StatusCode);
        Assert.Equal("symbol_not_declared", body.Value!.Code);
    }

    // -----------------------------------------------------------------------
    // PostAggregate error shapes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PostAggregate_AssetNotConfigured_Returns404_ErrorBodyShape()
    {
        var result = await AggregationEndpoints.PostAggregate(
            exchange: "binance",
            asset: "BTCUSDT",
            body: new AggregationEndpoints.AggregateRequest(
                SourceFeedId: "1h",
                TypeCode: "EqD",
                Threshold: 1000m,
                ThresholdUnit: "quote_asset",
                InputMode: "absolute",
                ConvenienceInput: null),
            options: Options(),
            planSource: new CollectionPlanHolder(),
            catalog: Substitute.For<IFeedCatalog>(),
            schema: Substitute.For<ISchemaManager>(),
            index: Substitute.For<IHistoryIndex>(),
            timeBarWakeup: Substitute.For<IJobWakeupQueue>(),
            tickWakeup: Substitute.For<IJobWakeupQueue>(),
            ct: Ct);

        var body = Assert.IsType<JsonHttpResult<ErrorBody>>(result);
        Assert.Equal(StatusCodes.Status404NotFound, body.StatusCode);
        Assert.Equal("asset_not_configured", body.Value!.Code);
    }

    [Fact]
    public async Task PostAggregate_FeedBusy_Returns409_FeedBusy()
    {
        var holder = new CollectionPlanHolder();
        holder.Publish(new CollectionPlan([CollectionAssets.Spot("BTCUSDT", 2)], [], []));

        var catalog = Substitute.For<IFeedCatalog>();
        // Kind=null + Columns=[] → TimeBarWithVolume; spot + no candle-ext → EqT/EqV/EqD eligible
        catalog.GetFeed(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedDefinition?>(new FeedDefinition { Columns = [] }));
        catalog.GetAsset(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AssetCatalogEntry?>(
                new AssetCatalogEntry("binance", "BTCUSDT", "BTCUSDT", AssetTypes.Spot, [])));

        var schema = Substitute.For<ISchemaManager>();
        schema.Load(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedMetadata?>(null));

        var index = Substitute.For<IHistoryIndex>();
        index.TryAcquireFeedGate(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedGateOutcome>(new FeedGateOutcome.Busy("active-999")));

        var result = await AggregationEndpoints.PostAggregate(
            exchange: "binance",
            asset: "BTCUSDT",
            body: new AggregationEndpoints.AggregateRequest(
                SourceFeedId: "1h",
                TypeCode: "EqD",
                Threshold: 1000m,
                ThresholdUnit: "quote_asset",
                InputMode: "absolute",
                ConvenienceInput: null),
            options: Options(),
            planSource: holder,
            catalog: catalog,
            schema: schema,
            index: index,
            timeBarWakeup: Substitute.For<IJobWakeupQueue>(),
            tickWakeup: Substitute.For<IJobWakeupQueue>(),
            ct: Ct);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, statusResult.StatusCode);
        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var code = valueResult.Value!.GetType().GetProperty("code")?.GetValue(valueResult.Value)?.ToString();
        Assert.Equal("feed_busy", code);
    }

    // -----------------------------------------------------------------------
    // GET /loads/{id} alias → unified job envelope (§3.5)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetLoadsAlias_UnknownJobId_Returns404_JobNotFound()
    {
        var index = Substitute.For<IHistoryIndex>();
        index.GetJob("unknown", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IndexJobRow?>(null));

        var result = await JobEndpoints.GetJob("unknown", index, Ct);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, statusResult.StatusCode);
        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var code = valueResult.Value!.GetType().GetProperty("code")?.GetValue(valueResult.Value)?.ToString();
        Assert.Equal("job_not_found", code);
    }

    [Fact]
    public async Task GetLoadsAlias_KnownJobId_ReturnsUnifiedEnvelope()
    {
        var index = Substitute.For<IHistoryIndex>();
        var row = new IndexJobRow(
            Id: "job-xyz", Kind: "load", State: "running",
            ProgressJson: "{}", Error: null,
            FeedKey: "binance|BTCUSDT|candles|1h",
            CancelRequested: false, TouchedJson: "{}", RequestJson: null);
        index.GetJob("job-xyz", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IndexJobRow?>(row));

        var result = await JobEndpoints.GetJob("job-xyz", index, Ct);

        var envelope = Assert.IsType<Ok<JobEnvelope>>(result);
        Assert.Equal("job-xyz", envelope.Value!.JobId);
    }
}
