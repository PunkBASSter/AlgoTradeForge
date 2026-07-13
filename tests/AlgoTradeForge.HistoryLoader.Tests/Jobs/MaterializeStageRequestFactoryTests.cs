using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using AlgoTradeForge.HistoryLoader.WebApi.Jobs;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Jobs;

/// <summary>
/// Unit tests for the REAL <see cref="MaterializeStageRequestFactory"/> (the worker tests stub it).
/// The factory is pure — it reads only <see cref="ICollectionPlanSource.Current"/> + the options
/// monitor — so no I/O fixture is needed. Fixture uses a CANONICAL AltBar derived id
/// (<c>EqV_ticks_1k</c>, source <c>ticks</c>) so <c>BuildAggregate</c>'s <see cref="AltBarFeedId"/>
/// parse succeeds and the produced <see cref="AggregationJob"/> mirrors what
/// <c>AggregationEndpoints.PostAggregate</c> builds for the same feed.
/// </summary>
public sealed class MaterializeStageRequestFactoryTests
{
    private const string DataRoot = "/data";

    private static readonly VenueInstrument _venue =
        new("BTCUSDT", "perpetual", "BTCUSDT_perp");

    // One on-demand tick source "ticks" + one derived AltBar "EqV_ticks_1k" sourced from it.
    private static readonly CollectionPlan _plan = new(
        Assets:
        [
            new CollectionAsset("binance", "BTC/USDT-PERP", _venue, 2,
                [new CollectionFeed("ticks", "", "on-demand", "csv", new DateOnly(2023, 5, 1))])
        ],
        Blocked: [],
        Warnings: [])
    {
        Derived = [new DerivedFeedEntry("binance", "BTC/USDT-PERP", _venue, "EqV_ticks_1k", "ticks")],
    };

    private static MaterializeStageRequestFactory BuildFactory()
    {
        var source = new CollectionPlanHolder();
        source.Publish(_plan);

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = DataRoot });

        return new MaterializeStageRequestFactory(source, options);
    }

    private static MaterializePlan ResolvePlan(DateRange? range) =>
        MaterializePlan.Resolve(_plan, "binance", "BTCUSDT", "EqV_ticks_1k", range);

    [Fact]
    public void BuildLoad_ExplicitRange_ResolvesTickSourceRequest()
    {
        var range = new DateRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 1));
        var plan = ResolvePlan(range);
        var loadStage = Assert.IsType<MaterializeStage.Load>(plan.Stages[0]);

        var req = BuildFactory().BuildLoad(plan, loadStage, "job-1");

        Assert.Equal("binance", req.Asset.Exchange);
        Assert.Equal("BTCUSDT_perp", req.Asset.Venue.Dir);
        Assert.Equal("ticks", req.FeedName);
        Assert.Equal("", req.Interval);
        Assert.Equal(new DateOnly(2024, 1, 1), req.From);
        Assert.Equal(new DateOnly(2024, 2, 1), req.To);
        Assert.Equal("job-1", req.JobId);
    }

    [Fact]
    public void BuildLoad_NullRange_FloorsAtSourceEffectiveStart_CeilsAtToday()
    {
        var plan = ResolvePlan(range: null);
        var loadStage = Assert.IsType<MaterializeStage.Load>(plan.Stages[0]);

        var req = BuildFactory().BuildLoad(plan, loadStage, "job-2");

        Assert.Equal("ticks", req.FeedName);
        Assert.Equal(new DateOnly(2023, 5, 1), req.From);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), req.To);
    }

    [Fact]
    public void BuildAggregate_MirrorsPostAggregateJobFields()
    {
        var plan = ResolvePlan(new DateRange(new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 1)));
        var aggStage = Assert.IsType<MaterializeStage.Aggregate>(plan.Stages[1]);

        var job = BuildFactory().BuildAggregate(plan, aggStage, "job-3").Job;

        // Reference values the aggregate accumulator consumes — derived the same way
        // AggregationEndpoints.PostAggregate does: parse the canonical id, resolve threshold
        // via the type-implied unit under the asset's scale.
        var parsed = AltBarFeedId.Parse("EqV_ticks_1k");
        var scale = AssetScaleContextFactory.FromDecimalDigits(2);
        var expectedScaled = ThresholdResolver.ResolveParsed(parsed.TypeCode, parsed.Threshold, scale);

        Assert.Equal("job-3", job.JobId);
        Assert.Equal("EqV_ticks_1k", job.OutcomeFeedId);
        Assert.Equal("EqV", job.TypeCode);
        Assert.Equal(1000m, job.ThresholdAbsolute);
        Assert.Equal(1000L, job.ThresholdScaled);
        Assert.Equal(expectedScaled, job.ThresholdScaled);
        Assert.Equal("base_asset", job.ThresholdUnit);
        Assert.Equal("convenience", job.ThresholdInputMode);
        Assert.Equal("1k", job.ThresholdConvenienceInput);

        // Source descriptor: the tick source, kind=Tick, rooted at the configured DataRoot.
        Assert.Equal(DataRoot, job.Source.DataRoot);
        Assert.Equal("binance", job.Source.Exchange);
        Assert.Equal("BTCUSDT_perp", job.Source.Asset);
        Assert.Equal("ticks", job.Source.FeedId);
        Assert.Equal(DataFeedKind.Tick, job.Source.Kind);

        Assert.Equal(Path.Combine(DataRoot, "binance", "BTCUSDT_perp"), job.AssetDir);
        Assert.Equal(100, job.MaxPartitionSizeMB);
        Assert.Null(job.Resume);
    }
}
