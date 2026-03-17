using System.Collections.Frozen;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class SymbolCollector
{
    private readonly FrozenDictionary<string, IFeedCollector> _collectors;
    private readonly ISettingsWriter _settingsWriter;
    private readonly ILogger<SymbolCollector> _logger;

    public SymbolCollector(
        IEnumerable<IFeedCollector> collectors,
        ISettingsWriter settingsWriter,
        ILogger<SymbolCollector> logger)
    {
        _collectors = collectors.ToFrozenDictionary(c => c.FeedName);
        _settingsWriter = settingsWriter;
        _logger = logger;
    }

    public async Task CollectFeedAsync(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var feedName = feedConfig.Name;

        if (!_collectors.TryGetValue(feedName, out var collector))
        {
            _logger.LogWarning("Unknown feed: {Feed} for {Symbol}", feedName, assetConfig.Symbol);
            return;
        }

        // Spot assets only support feeds that declare SupportsSpot.
        if (AssetTypes.IsSpot(assetConfig.Type) && !collector.SupportsSpot)
        {
            _logger.LogWarning(
                "Spot assets do not support {Feed}, skipping for {Symbol}",
                feedName, assetConfig.Symbol);
            return;
        }

        _logger.LogInformation(
            "Collecting {Feed}/{Interval} for {Symbol} from {From} to {To}",
            feedName, feedConfig.Interval, assetConfig.Symbol, fromMs, toMs);

        try
        {
            await CollectWithDateDiscoveryAsync(
                collector, assetConfig, feedConfig, assetDir, fromMs, toMs, ct);
        }
        catch (DataSourceApiException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.BadRequest && !ex.IsDateRangeError)
        {
            _logger.LogWarning(
                "API error for {Symbol}/{Feed}: {Code} {Msg}, skipping",
                assetConfig.Symbol, feedName, ex.ApiErrorCode, ex.ApiErrorMessage);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.BadRequest
                          or System.Net.HttpStatusCode.Forbidden
                          or System.Net.HttpStatusCode.NotFound
                          or (System.Net.HttpStatusCode)451)
        {
            _logger.LogWarning(
                "HTTP {StatusCode} for {Symbol}/{Feed}, skipping (may be delisted or endpoint removed)",
                (int?)ex.StatusCode, assetConfig.Symbol, feedConfig.Name);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.InternalServerError
                          or System.Net.HttpStatusCode.BadGateway
                          or System.Net.HttpStatusCode.ServiceUnavailable
                          or System.Net.HttpStatusCode.GatewayTimeout)
        {
            _logger.LogWarning(
                "HTTP {StatusCode} for {Symbol}/{Feed}, transient server error — skipping",
                (int?)ex.StatusCode, assetConfig.Symbol, feedName);
        }
    }

    private async Task CollectWithDateDiscoveryAsync(
        IFeedCollector collector,
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        // Fast path: try full collection with configured start date.
        try
        {
            await collector.CollectAsync(assetConfig, feedConfig, assetDir, fromMs, toMs, ct);
            return;
        }
        catch (DataSourceApiException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.BadRequest && ex.IsDateRangeError)
        {
            _logger.LogInformation(
                "Date too early for {Symbol}/{Feed}/{Interval}, searching for valid start",
                assetConfig.Symbol, feedConfig.Name, feedConfig.Interval);
        }

        // Binary search for the earliest valid month.
        long discovered = await BinarySearchStartAsync(
            collector, assetConfig, feedConfig, assetDir, fromMs, toMs, ct);

        if (discovered < 0)
        {
            _logger.LogWarning(
                "No valid start date found for {Symbol}/{Feed}/{Interval}",
                assetConfig.Symbol, feedConfig.Name, feedConfig.Interval);
            return;
        }

        var discoveredDate = DateOnly.FromDateTime(
            DateTimeOffset.FromUnixTimeMilliseconds(discovered).UtcDateTime);
        _logger.LogInformation(
            "Discovered earliest date {Date} for {Symbol}/{Feed}/{Interval}",
            discoveredDate, assetConfig.Symbol, feedConfig.Name, feedConfig.Interval);

        // Full collection from the discovered start.
        await collector.CollectAsync(assetConfig, feedConfig, assetDir, discovered, toMs, ct);

        _settingsWriter.UpdateFeedHistoryStart(
            assetConfig.Symbol, assetConfig.Type,
            feedConfig.Name, feedConfig.Interval, discoveredDate);
    }

    /// <summary>
    /// Binary searches month-by-month between <paramref name="fromMs"/> and
    /// <paramref name="toMs"/> using 1-month probe windows. Returns the Unix-ms
    /// of the earliest valid first-of-month, or -1 if no valid month was found.
    /// Worst case: ~log2(months) API calls instead of linear probing.
    /// </summary>
    private async Task<long> BinarySearchStartAsync(
        IFeedCollector collector,
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
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
                await collector.CollectAsync(
                    assetConfig, feedConfig, assetDir, midMs, probeEndMs, ct);

                // mid works — record it and search earlier.
                result = mid;
                high = mid - 1;

                _logger.LogDebug(
                    "Probe succeeded at {Date} for {Symbol}/{Feed}",
                    DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(midMs).UtcDateTime),
                    assetConfig.Symbol, feedConfig.Name);
            }
            catch (DataSourceApiException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.BadRequest && ex.IsDateRangeError)
            {
                // mid is too early — search later.
                low = mid + 1;

                _logger.LogDebug(
                    "Probe at {Date} too early for {Symbol}/{Feed}: {Msg}",
                    DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(midMs).UtcDateTime),
                    assetConfig.Symbol, feedConfig.Name, ex.ApiErrorMessage);
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
