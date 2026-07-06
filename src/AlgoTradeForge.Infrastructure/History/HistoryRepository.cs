using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class HistoryRepository(
    IInt64BarLoader barLoader,
    IFileStorage storage,
    IOptions<CandleStorageOptions> storageOptions,
    ILogger<HistoryRepository> logger) : IHistoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    //TODO: investigate if upcast is required
    public Task<TimeSeries<Int64Bar>> Load(DataFeedSubscription subscription, DateOnly from, DateOnly to, CancellationToken ct = default)
        => LoadTimeBar(subscription.RequireAsset(), ((TimeBarSubscription)subscription).TimeFrame, from, to, ct);

    public Task<TimeSeries<Int64Bar>> Load(Asset asset, DataFeedSubscription subscription, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var dataRoot = storageOptions.Value.DataRoot;
        var assetDir = AssetDirectoryName.From(asset);

        return subscription switch
        {
            TimeBarSubscription tb => LoadTimeBar(asset, tb.TimeFrame, from, to, ct),
            AltBarSubscription ab => barLoader.Load(
                new DataFeedDescriptor(dataRoot, asset.Exchange, assetDir, ab.FeedId, DataFeedKind.AltBar),
                from, to, ct),
            TickSubscription => barLoader.Load(
                new DataFeedDescriptor(dataRoot, asset.Exchange, assetDir, "ticks", DataFeedKind.Tick),
                from, to, ct),
            SideFeedSubscription => throw new ArgumentException(
                "Side feeds cannot be loaded as a primary OHLCV series. " +
                "Side feeds are FeedSeries, not TimeSeries<Int64Bar> — bind them via IFeedContext / FeedContextBuilder.",
                nameof(subscription)),
            _ => throw new ArgumentOutOfRangeException(nameof(subscription),
                $"Unknown DataFeedSubscription subtype: {subscription.GetType().Name}"),
        };
    }

    private async Task<TimeSeries<Int64Bar>> LoadTimeBar(Asset asset, TimeFrame timeFrame, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var dataRoot = storageOptions.Value.DataRoot;
        var assetDir = AssetDirectoryName.From(asset);
        var sourceInterval = storageOptions.Value.SourceInterval;
        var sourceCode = TimeFrameFormatter.Format(sourceInterval);

        var intervals = await ReadCandleIntervals(dataRoot, asset.Exchange, assetDir, ct);
        var (loadCode, resample) = ChooseSourceFeed(intervals, timeFrame, sourceInterval, sourceCode);

        var descriptor = new DataFeedDescriptor(dataRoot, asset.Exchange, assetDir, loadCode, DataFeedKind.TimeBar);
        var raw = await barLoader.Load(descriptor, from, to, ct);
        return resample ? raw.Resample(timeFrame) : raw;
    }

    // Decides which on-disk feed to read and whether to resample it to the requested timeframe.
    //  - No manifest, or the fine-grained source interval (1m) is present → legacy crypto path:
    //    load the source and resample (preserves reproducibility even when a native 1d also exists).
    //  - Source interval absent (e.g. the equity archive: 5m/1d only) → load the requested
    //    timeframe natively when present, else resample from the largest available interval that
    //    cleanly divides the request (e.g. 1h from 5m). Throws when nothing on disk can produce it.
    private static (string LoadCode, bool Resample) ChooseSourceFeed(
        string[]? intervals, TimeFrame requested, TimeSpan sourceInterval, string sourceCode)
    {
        var hasSource = intervals is null || intervals.Length == 0
            || intervals.Contains(sourceCode, StringComparer.Ordinal);

        if (hasSource)
        {
            if (requested < sourceInterval)
                throw new ArgumentException(
                    $"Requested timeframe ({requested}) is smaller than the source interval ({sourceInterval}).",
                    nameof(requested));
            return (sourceCode, requested.Duration != sourceInterval);
        }

        var declared = intervals!;
        if (declared.Contains(requested.Code, StringComparer.Ordinal))
            return (requested.Code, false);

        var divisor = declared
            .Select(code => (code, dur: TryParseDuration(code)))
            .Where(x => x.dur is { } d && d < requested.Duration && requested.Duration.Ticks % d.Ticks == 0)
            .OrderByDescending(x => x.dur!.Value)
            .Select(x => x.code)
            .FirstOrDefault();

        if (divisor is not null)
            return (divisor, true);

        throw new ArgumentException(
            $"No feed on disk can produce timeframe '{requested.Code}': available intervals = " +
            $"[{string.Join(", ", declared)}]. It is neither present natively nor a clean multiple " +
            "of a finer available interval.",
            nameof(requested));
    }

    private static TimeSpan? TryParseDuration(string intervalCode) =>
        TimeFrame.TryParse(intervalCode, out var tf) ? tf.Duration : null;

    // Available candle intervals from the asset's feeds.json, or null when absent/unreadable
    // (callers fall back to the source-resample path).
    private async Task<string[]?> ReadCandleIntervals(string dataRoot, string exchange, string assetDir, CancellationToken ct)
    {
        var feedsJsonPath = Path.Combine(dataRoot, exchange, assetDir, "feeds.json");
        if (!await storage.Exists(feedsJsonPath, ct)) return null;
        try
        {
            await using var stream = await storage.OpenRead(feedsJsonPath, ct);
            var metadata = await JsonSerializer.DeserializeAsync<FeedMetadata>(stream, JsonOptions, ct);
            return metadata?.Candles?.Intervals;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Surface the corrupt manifest — otherwise the source-interval fallback can silently
            // produce 0 bars (the exact failure class this native-load path was added to fix).
            logger.LogWarning(ex, "Unreadable feeds.json at {Path}; falling back to source-interval load", feedsJsonPath);
            return null;
        }
    }
}
