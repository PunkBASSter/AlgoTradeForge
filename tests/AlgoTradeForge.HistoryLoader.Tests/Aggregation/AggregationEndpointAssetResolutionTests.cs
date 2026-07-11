using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// Verifies that the aggregation endpoints resolve the asset via <see cref="ICollectionPlanSource"/>
/// rather than <c>HistoryLoaderOptions.Assets</c>.
/// </summary>
public sealed class AggregationEndpointAssetResolutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IOptionsMonitor<HistoryLoaderOptions> Options(string dataRoot = "/test-root")
    {
        var m = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        m.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = dataRoot });
        return m;
    }

    private static ICollectionPlanSource EmptyPlan() => new CollectionPlanHolder();

    // -------------------------------------------------------------------------
    // 404 when asset is absent from plan
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostAggregate_AssetNotInPlan_Returns404_WithWireCompatibleErrorCode()
    {
        var plan = EmptyPlan();
        var catalog = Substitute.For<IFeedCatalog>();
        var schema = Substitute.For<ISchemaManager>();
        var registry = Substitute.For<IAggregationJobRegistry>();
        var queue = Substitute.For<IAggregationJobQueue>();
        var tickQueue = Substitute.For<IAggregationTickJobQueue>();

        var result = await AggregationEndpoints.PostAggregate(
            exchange: "binance",
            asset: "BTCUSDT",
            body: new AggregationEndpoints.AggregateRequest(
                SourceFeedId: "1h",
                TypeCode: "EqV",
                Threshold: 1000m,
                ThresholdUnit: "quote_asset",
                InputMode: "absolute",
                ConvenienceInput: null),
            options: Options(),
            planSource: plan,
            catalog: catalog,
            schema: schema,
            registry: registry,
            queue: queue,
            tickQueue: tickQueue,
            ct: Ct);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, statusResult.StatusCode);
        var json = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var err = json.Value!.GetType().GetProperty("error")!.GetValue(json.Value)!.ToString();
        Assert.Equal("asset_not_configured", err);
    }

    [Fact]
    public async Task PostAggregate_AssetInPlan_ProceedsToSourceFeedLookup()
    {
        var holder = new CollectionPlanHolder();
        holder.Publish(new CollectionPlan([CollectionAssets.Spot("BTCUSDT", 2)], [], []));
        var plan = (ICollectionPlanSource)holder;
        var catalog = Substitute.For<IFeedCatalog>();
        catalog.GetFeed(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedDefinition?>(null));
        var schema = Substitute.For<ISchemaManager>();
        var registry = Substitute.For<IAggregationJobRegistry>();
        var queue = Substitute.For<IAggregationJobQueue>();
        var tickQueue = Substitute.For<IAggregationTickJobQueue>();

        var result = await AggregationEndpoints.PostAggregate(
            exchange: "binance",
            asset: "BTCUSDT",
            body: new AggregationEndpoints.AggregateRequest(
                SourceFeedId: "1h",
                TypeCode: "EqV",
                Threshold: 1000m,
                ThresholdUnit: "quote_asset",
                InputMode: "absolute",
                ConvenienceInput: null),
            options: Options(),
            planSource: plan,
            catalog: catalog,
            schema: schema,
            registry: registry,
            queue: queue,
            tickQueue: tickQueue,
            ct: Ct);

        // Asset found in plan → proceeds to source feed lookup → 422 source_feed_not_found
        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
    }

    [Fact]
    public async Task DeleteFeed_AssetNotInPlan_Returns404_WithWireCompatibleErrorCode()
    {
        var plan = EmptyPlan();
        var schema = Substitute.For<ISchemaManager>();
        var registry = Substitute.For<IAggregationJobRegistry>();

        var result = await AggregationEndpoints.DeleteFeed(
            exchange: "binance",
            asset: "BTCUSDT",
            feedId: "EqV_1h_1000",
            options: Options(),
            planSource: plan,
            schema: schema,
            registry: registry,
            ct: Ct);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, statusResult.StatusCode);
        var json = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var err = json.Value!.GetType().GetProperty("error")!.GetValue(json.Value)!.ToString();
        Assert.Equal("asset_not_configured", err);
    }
}
