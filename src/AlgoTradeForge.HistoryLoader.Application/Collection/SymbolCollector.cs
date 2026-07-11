using System.Collections.Frozen;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class SymbolCollector
{
    private readonly FrozenDictionary<string, IFeedCollector> _collectors;
    private readonly ArchiveBackfillService _archiveBackfill;
    private readonly IHistoryIndex _index;
    private readonly CollectionChangeNotifier _notifier;
    private readonly ILogger<SymbolCollector> _logger;

    public SymbolCollector(
        IEnumerable<IFeedCollector> collectors,
        ArchiveBackfillService archiveBackfill,
        IHistoryIndex index,
        CollectionChangeNotifier notifier,
        ILogger<SymbolCollector> logger)
    {
        _collectors = collectors.ToFrozenDictionary(c => c.FeedName);
        _archiveBackfill = archiveBackfill;
        _index = index;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task CollectFeed(
        CollectionAsset asset,
        CollectionFeed feed,
        string assetDir,
        long fromMs,
        long toMs,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        var feedName = feed.FeedName;

        // Collector may be absent: materializer-only feeds (e.g. taker-volume, whose live
        // collector was retired) are archive-sourced and have no REST tail. The archive path
        // therefore runs BEFORE the collector gate — a null collector must NOT short-circuit it.
        _collectors.TryGetValue(feedName, out var collector);

        // Spot assets only support feeds that declare SupportsSpot. Archive materializers enforce
        // their own Supports() inside CoverFromArchive, so this gate applies only to live collectors.
        if (collector is not null && AssetTypes.IsSpot(asset.Venue.AssetType) && !collector.SupportsSpot)
        {
            _logger.LogWarning(
                "Spot assets do not support {Feed}, skipping for {Symbol}",
                feedName, asset.Venue.ApiSymbol);
            return;
        }

        fromMs = await _archiveBackfill.CoverFromArchive(asset, feed, assetDir, fromMs, toMs, progress, ct);
        if (fromMs >= toMs)
            return; // fully covered by archive — no REST tail needed

        if (collector is null)
        {
            // No live source for the current-month REST tail — archive owns closed months only.
            _logger.LogInformation(
                "No live collector for {Feed}/{Symbol}; archive-only, REST tail skipped",
                feedName, asset.Venue.ApiSymbol);
            return;
        }

        _logger.LogInformation(
            "Collecting {Feed}/{Interval} for {Symbol} from {From} to {To}",
            feedName, feed.Interval, asset.Venue.ApiSymbol, fromMs, toMs);

        try
        {
            await CollectWithDateDiscovery(
                collector, asset, feed, assetDir, fromMs, toMs, ct);
        }
        catch (DataSourceApiException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.BadRequest && !ex.IsDateRangeError)
        {
            _logger.LogWarning(
                "API error for {Symbol}/{Feed}: {Code} {Msg}, skipping",
                asset.Venue.ApiSymbol, feedName, ex.ApiErrorCode, ex.ApiErrorMessage);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.BadRequest
                          or System.Net.HttpStatusCode.Forbidden
                          or System.Net.HttpStatusCode.NotFound
                          or (System.Net.HttpStatusCode)451)
        {
            _logger.LogWarning(
                "HTTP {StatusCode} for {Symbol}/{Feed}, skipping (may be delisted or endpoint removed)",
                (int?)ex.StatusCode, asset.Venue.ApiSymbol, feed.FeedName);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.InternalServerError
                          or System.Net.HttpStatusCode.BadGateway
                          or System.Net.HttpStatusCode.ServiceUnavailable
                          or System.Net.HttpStatusCode.GatewayTimeout)
        {
            _logger.LogWarning(
                "HTTP {StatusCode} for {Symbol}/{Feed}, transient server error — skipping",
                (int?)ex.StatusCode, asset.Venue.ApiSymbol, feedName);
        }
    }

    private async Task CollectWithDateDiscovery(
        IFeedCollector collector,
        CollectionAsset asset,
        CollectionFeed feed,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        // Fast path: try full collection with configured start date.
        try
        {
            await collector.Collect(asset, feed, assetDir, fromMs, toMs, ct);
            return;
        }
        catch (DataSourceApiException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.BadRequest && ex.IsDateRangeError)
        {
            _logger.LogInformation(
                "Date too early for {Symbol}/{Feed}/{Interval}, searching for valid start",
                asset.Venue.ApiSymbol, feed.FeedName, feed.Interval);
        }

        // Binary search for the earliest valid month.
        long discovered = await BinarySearchStart(
            collector, asset, feed, assetDir, fromMs, toMs, ct);

        if (discovered < 0)
        {
            _logger.LogWarning(
                "No valid start date found for {Symbol}/{Feed}/{Interval}",
                asset.Venue.ApiSymbol, feed.FeedName, feed.Interval);
            return;
        }

        var discoveredDate = DateOnly.FromDateTime(
            DateTimeOffset.FromUnixTimeMilliseconds(discovered).UtcDateTime);
        _logger.LogInformation(
            "Discovered earliest date {Date} for {Symbol}/{Feed}/{Interval}",
            discoveredDate, asset.Venue.ApiSymbol, feed.FeedName, feed.Interval);

        // Full collection from the discovered start.
        await collector.Collect(asset, feed, assetDir, discovered, toMs, ct);

        await _index.SetDiscoveredFirstMonth(
            asset.Exchange, asset.Venue.Dir,
            feed.FeedName, feed.Interval,
            $"{discoveredDate.Year:D4}-{discoveredDate.Month:D2}", ct);
        _notifier.NotifyDiscoveryRecorded();
    }

    /// <summary>
    /// Binary searches month-by-month between <paramref name="fromMs"/> and
    /// <paramref name="toMs"/> using 1-month probe windows. Returns the Unix-ms
    /// of the earliest valid first-of-month, or -1 if no valid month was found.
    /// Worst case: ~log2(months) API calls instead of linear probing.
    /// </summary>
    private async Task<long> BinarySearchStart(
        IFeedCollector collector,
        CollectionAsset asset,
        CollectionFeed feed,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        int low = ToMonthIndex(fromMs);
        int high = ToMonthIndex(toMs);
        int result = -1;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            long midMs = FromMonthIndex(mid);
            long probeEndMs = Math.Min(FromMonthIndex(mid + 1), toMs);

            try
            {
                await collector.Collect(
                    asset, feed, assetDir, midMs, probeEndMs, ct);

                // mid works — record it and search earlier.
                result = mid;
                high = mid - 1;

                _logger.LogDebug(
                    "Probe succeeded at {Date} for {Symbol}/{Feed}",
                    DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(midMs).UtcDateTime),
                    asset.Venue.ApiSymbol, feed.FeedName);
            }
            catch (DataSourceApiException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.BadRequest && ex.IsDateRangeError)
            {
                // mid is too early — search later.
                low = mid + 1;

                _logger.LogDebug(
                    "Probe at {Date} too early for {Symbol}/{Feed}: {Msg}",
                    DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(midMs).UtcDateTime),
                    asset.Venue.ApiSymbol, feed.FeedName, ex.ApiErrorMessage);
            }
            // Non-date-range errors propagate to the outer catch blocks.
        }

        return result >= 0 ? FromMonthIndex(result) : -1;
    }

    internal static int ToMonthIndex(long unixMs)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        return dt.Year * 12 + (dt.Month - 1);
    }

    internal static long FromMonthIndex(int monthIndex)
    {
        int year = monthIndex / 12;
        int month = monthIndex % 12 + 1;
        return new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }
}
