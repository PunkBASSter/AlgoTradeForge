using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class HistoryRepository(
    IInt64BarLoader barLoader,
    IFileStorage storage,
    IOptions<CandleStorageOptions> storageOptions) : IHistoryRepository
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
        var requestedCode = timeFrame.Code;

        // Default path (crypto): a fine-grained source interval (e.g. 1m) exists on disk — load
        // it and resample, preserving reproducibility of existing runs. Only when the source
        // interval is ABSENT (e.g. the Stooq equity archive carries 5m/1d but no 1m) do we load
        // the requested timeframe's native partitions directly.
        var intervals = await ReadCandleIntervals(dataRoot, asset.Exchange, assetDir, ct);
        var loadNative = intervals is not null
            && !intervals.Contains(sourceCode, StringComparer.Ordinal)
            && intervals.Contains(requestedCode, StringComparer.Ordinal);

        if (loadNative)
        {
            var native = new DataFeedDescriptor(dataRoot, asset.Exchange, assetDir, requestedCode, DataFeedKind.TimeBar);
            return await barLoader.Load(native, from, to, ct);
        }

        if (timeFrame < sourceInterval)
            throw new ArgumentException(
                $"Requested timeframe ({timeFrame}) is smaller than the asset's smallest interval ({sourceInterval}).",
                nameof(timeFrame));

        var descriptor = new DataFeedDescriptor(dataRoot, asset.Exchange, assetDir, sourceCode, DataFeedKind.TimeBar);
        var raw = await barLoader.Load(descriptor, from, to, ct);

        return timeFrame == sourceInterval ? raw : raw.Resample(timeFrame);
    }

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
            return null;
        }
    }
}
