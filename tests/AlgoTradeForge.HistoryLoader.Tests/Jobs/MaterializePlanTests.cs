using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Jobs;

public sealed class MaterializePlanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VenueInstrument _venue =
        new("BTCUSDT", AssetTypes.Perpetual, "BTCUSDT_perp");

    // _plan has:
    //   - one on-demand collected feed "agg-trades" (interval-less ticks)
    //   - one derived feed "EqV_1k" whose source is "agg-trades"
    private static readonly CollectionPlan _plan = new(
        Assets:
        [
            new CollectionAsset(
                "binance", "BTC/USDT-PERP", _venue, 2,
                [new CollectionFeed("agg-trades", "", "on-demand", "csv", new DateOnly(2023, 1, 1))])
        ],
        Blocked: [],
        Warnings: [])
    {
        Derived =
        [
            new DerivedFeedEntry("binance", "BTC/USDT-PERP", _venue, "EqV_1k", "agg-trades")
        ]
    };

    // -------------------------------------------------------------------------
    // MaterializePlan.Resolve — plan resolution
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_DerivedFeed_TwoStages_OnDemandFeed_OneStage()
    {
        var derived = MaterializePlan.Resolve(_plan, "binance", "BTCUSDT", "EqV_1k", range: null);
        Assert.Equal(2, derived.Stages.Count);
        Assert.IsType<MaterializeStage.Load>(derived.Stages[0]);
        Assert.IsType<MaterializeStage.Aggregate>(derived.Stages[1]);

        var onDemand = MaterializePlan.Resolve(_plan, "binance", "BTCUSDT", "agg-trades", range: null);
        Assert.Single(onDemand.Stages);
        Assert.IsType<MaterializeStage.Load>(onDemand.Stages[0]);
    }

    [Fact]
    public void Resolve_DerivedFeed_StagesCarryCorrectFeedKeys()
    {
        var plan = MaterializePlan.Resolve(_plan, "binance", "BTCUSDT", "EqV_1k", range: null);

        var loadStage = Assert.IsType<MaterializeStage.Load>(plan.Stages[0]);
        Assert.Equal("binance|BTCUSDT_perp|agg-trades|", loadStage.FeedKey);

        var aggStage = Assert.IsType<MaterializeStage.Aggregate>(plan.Stages[1]);
        Assert.Equal("binance|BTCUSDT_perp|EqV_1k|", aggStage.FeedKey);

        Assert.Equal("binance|BTCUSDT_perp|EqV_1k|", plan.OutputFeedKey);
    }

    [Fact]
    public void Resolve_OnDemandFeed_StageCarriesCorrectFeedKey()
    {
        var plan = MaterializePlan.Resolve(_plan, "binance", "BTCUSDT", "agg-trades", range: null);

        var loadStage = Assert.IsType<MaterializeStage.Load>(plan.Stages[0]);
        Assert.Equal("binance|BTCUSDT_perp|agg-trades|", loadStage.FeedKey);

        Assert.Equal("binance|BTCUSDT_perp|agg-trades|", plan.OutputFeedKey);
    }

    [Fact]
    public void Resolve_UnknownFeed_ThrowsFeedNotMaterializableException()
    {
        Assert.Throws<FeedNotMaterializableException>(() =>
            MaterializePlan.Resolve(_plan, "binance", "BTCUSDT", "nonexistent", range: null));
    }

    [Fact]
    public void Resolve_EagerFeed_ThrowsFeedNotMaterializableException()
    {
        var plan = new CollectionPlan(
            Assets:
            [
                new CollectionAsset(
                    "binance", "BTC/USDT-PERP", _venue, 2,
                    [new CollectionFeed("1h", "1h", "eager", "csv", new DateOnly(2023, 1, 1))])
            ],
            Blocked: [],
            Warnings: []);

        Assert.Throws<FeedNotMaterializableException>(() =>
            MaterializePlan.Resolve(plan, "binance", "BTCUSDT", "1h", range: null));
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/materialize endpoint
    // -------------------------------------------------------------------------

    private static ICollectionPlanSource PlanSource(CollectionPlan plan)
    {
        var holder = new CollectionPlanHolder();
        holder.Publish(plan);
        return holder;
    }

    [Fact]
    public async Task PostMaterialize_Acquired_Returns202WithJobIdAndLocation()
    {
        var index = Substitute.For<IHistoryIndex>();
        var wakeup = Substitute.For<IJobWakeupQueue>();

        index.TryAcquireFeedGate(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedGateOutcome>(new FeedGateOutcome.Acquired("job-abc")));
        wakeup.TryEnqueue("job-abc").Returns(true);

        var result = await MaterializeEndpoints.PostMaterialize(
            new MaterializeEndpoints.MaterializeRequest("binance", "BTCUSDT", "EqV_1k"),
            PlanSource(_plan), index, wakeup, Ct);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, status.StatusCode);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var value = valueResult.Value!;
        var jobId = value.GetType().GetProperty("job_id")!.GetValue(value)!.ToString();
        var location = value.GetType().GetProperty("location")!.GetValue(value)!.ToString();
        Assert.Equal("job-abc", jobId);
        Assert.Equal("/api/v1/jobs/job-abc/progress", location);
    }

    [Fact]
    public async Task PostMaterialize_Busy_Returns409WithFeedBusy()
    {
        var index = Substitute.For<IHistoryIndex>();
        var wakeup = Substitute.For<IJobWakeupQueue>();

        index.TryAcquireFeedGate(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedGateOutcome>(new FeedGateOutcome.Busy("existing-job")));

        var result = await MaterializeEndpoints.PostMaterialize(
            new MaterializeEndpoints.MaterializeRequest("binance", "BTCUSDT", "EqV_1k"),
            PlanSource(_plan), index, wakeup, Ct);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, status.StatusCode);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var value = valueResult.Value!;
        var code = value.GetType().GetProperty("code")!.GetValue(value)!.ToString();
        Assert.Equal("feed_busy", code);
    }

    [Fact]
    public async Task PostMaterialize_UnknownFeed_Returns422FeedNotMaterializable()
    {
        var index = Substitute.For<IHistoryIndex>();
        var wakeup = Substitute.For<IJobWakeupQueue>();

        var result = await MaterializeEndpoints.PostMaterialize(
            new MaterializeEndpoints.MaterializeRequest("binance", "BTCUSDT", "nonexistent-feed"),
            PlanSource(_plan), index, wakeup, Ct);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status.StatusCode);

        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var value = valueResult.Value!;
        var code = value.GetType().GetProperty("code")!.GetValue(value)!.ToString();
        Assert.Equal("feed_not_materializable", code);
    }
}
