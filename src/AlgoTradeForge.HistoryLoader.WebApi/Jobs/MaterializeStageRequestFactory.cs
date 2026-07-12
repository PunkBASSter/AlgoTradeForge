using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Jobs;

internal sealed class MaterializeStageRequestFactory(
    ICollectionPlanSource planSource,
    IOptionsMonitor<HistoryLoaderOptions> options) : IMaterializeStageRequestFactory
{
    public ArchiveLoadRequest BuildLoad(MaterializePlan plan, MaterializeStage.Load stage, string jobId)
    {
        var (xch, dir, feedName, interval) = ParseFeedKey(stage.FeedKey);
        var asset = ResolveAsset(xch, dir);
        var (from, to) = ResolveRange(plan, asset, feedName);
        return new ArchiveLoadRequest(asset, feedName, interval, from, to, jobId);
    }

    public AggregationRunRequest BuildAggregate(MaterializePlan plan, MaterializeStage.Aggregate stage, string jobId)
    {
        var (xch, dir, outcomeFeedId, _) = ParseFeedKey(stage.FeedKey);
        var asset = ResolveAsset(xch, dir);

        var parsed = AltBarFeedId.Parse(outcomeFeedId);
        var unit = ThresholdResolver.GetImplicitUnit(parsed.TypeCode);
        var scale = AssetScaleContextFactory.FromDecimalDigits(asset.DecimalDigits);
        var canonical = parsed.Threshold.ToCanonicalString();
        var threshold = ThresholdResolver.Resolve(unit, inputMode: "convenience",
            thresholdValue: null, convenienceInput: canonical, scale: scale);

        var opts = options.CurrentValue;
        var assetDir = BackfillOrchestrator.ResolveAssetDir(opts.DataRoot, asset);
        var (sourceFeedId, sourceKind) = ResolveSource(plan);

        var job = new AggregationJob(
            JobId: jobId,
            Source: new DataFeedDescriptor(opts.DataRoot, xch, dir, sourceFeedId, sourceKind),
            AssetDir: assetDir,
            OutcomeFeedId: outcomeFeedId,
            TypeCode: parsed.TypeCode,
            ThresholdAbsolute: threshold.Absolute,
            ThresholdScaled: threshold.Scaled,
            ThresholdUnit: unit,
            ThresholdInputMode: "convenience",
            ThresholdConvenienceInput: canonical,
            SourceScale: scale,
            AccumulatorScale: scale,
            MaxPartitionSizeMB: opts.Aggregator.MaxPartitionSizeMB,
            ToolVersion: typeof(MaterializeStageRequestFactory).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Resume: null);

        return new AggregationRunRequest(job);
    }

    // Derived aggregation reads its source from the plan's Load stage (stage 0 = the source feed).
    private (string FeedId, DataFeedKind Kind) ResolveSource(MaterializePlan plan)
    {
        var load = plan.Stages.OfType<MaterializeStage.Load>().FirstOrDefault();
        if (load is null)
            return (FeedNames.Ticks, DataFeedKind.Tick);

        var (_, _, feedName, _) = ParseFeedKey(load.FeedKey);
        var isTick = string.Equals(feedName, FeedNames.Ticks, StringComparison.Ordinal);
        return isTick ? (FeedNames.Ticks, DataFeedKind.Tick) : (feedName, DataFeedKind.TimeBar);
    }

    private CollectionAsset ResolveAsset(string exchange, string dir) =>
        planSource.Current.Assets.FirstOrDefault(a =>
            string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Venue.Dir, dir, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"Materialize references {exchange}/{dir}, no longer declared in any enabled group.");

    // Null materialize range = "all available": floor at the source feed's effective start (else a
    // conservative early Binance date), ceiling at today.
    private static (DateOnly From, DateOnly To) ResolveRange(
        MaterializePlan plan, CollectionAsset asset, string feedName)
    {
        if (plan.Range is { } r)
            return (r.From, r.To);

        var effective = asset.Feeds
            .FirstOrDefault(f => string.Equals(f.FeedName, feedName, StringComparison.Ordinal))?.EffectiveStart
            ?? new DateOnly(2017, 1, 1);
        return (effective, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    // feedKey shape: {exchange}|{dir}|{feedName}|{interval} — interval may be empty for
    // interval-less feeds (aggregation outcomes, ticks).
    private static (string Exchange, string Dir, string FeedName, string Interval) ParseFeedKey(string feedKey)
    {
        var parts = feedKey.Split('|');
        if (parts.Length < 4)
            throw new InvalidOperationException($"Malformed materialize feed key '{feedKey}'.");
        return (parts[0], parts[1], parts[2], parts[3]);
    }
}
