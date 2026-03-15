using System.Collections.Frozen;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class SymbolCollector
{
    /// <summary>Max monthly advances (24 months = 2 years of probing).</summary>
    internal const int MaxDateAdvances = 24;

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

        var currentFromMs = fromMs;
        int advances = 0;

        while (true)
        {
            try
            {
                await collector.CollectAsync(assetConfig, feedConfig, assetDir, currentFromMs, toMs, ct);

                // Success after advancing → persist the discovered start date.
                if (advances > 0)
                {
                    var discoveredDate = DateOnly.FromDateTime(
                        DateTimeOffset.FromUnixTimeMilliseconds(currentFromMs).UtcDateTime);
                    _logger.LogInformation(
                        "Discovered earliest date {Date} for {Symbol}/{Feed}/{Interval} after {Advances} advance(s)",
                        discoveredDate, assetConfig.Symbol, feedName, feedConfig.Interval, advances);
                    _settingsWriter.UpdateFeedHistoryStart(
                        assetConfig.Symbol, assetConfig.Type,
                        feedName, feedConfig.Interval, discoveredDate);
                }

                return;
            }
            catch (DataSourceApiException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.BadRequest
                && !ex.IsParameterValidationError)
            {
                advances++;
                if (advances > MaxDateAdvances)
                {
                    _logger.LogWarning(
                        "Exhausted {Max} date advances for {Symbol}/{Feed}/{Interval}, giving up",
                        MaxDateAdvances, assetConfig.Symbol, feedName, feedConfig.Interval);
                    return;
                }

                currentFromMs = AdvanceOneMonth(currentFromMs);
                _logger.LogInformation(
                    "Date-range error for {Symbol}/{Feed}/{Interval}: {Msg} — advancing to {From}",
                    assetConfig.Symbol, feedName, feedConfig.Interval,
                    ex.ApiErrorMessage, currentFromMs);
            }
            catch (DataSourceApiException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.BadRequest
                && ex.IsParameterValidationError)
            {
                _logger.LogWarning(
                    "Parameter validation error for {Symbol}/{Feed}: {Code} {Msg}, skipping",
                    assetConfig.Symbol, feedName, ex.ApiErrorCode, ex.ApiErrorMessage);
                return;
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.BadRequest
                              or System.Net.HttpStatusCode.Forbidden
                              or System.Net.HttpStatusCode.NotFound
                              or (System.Net.HttpStatusCode)451)
            {
                _logger.LogWarning(
                    "HTTP {StatusCode} for {Symbol}, skipping (may be delisted or restricted)",
                    (int?)ex.StatusCode, assetConfig.Symbol);
                return;
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
                return;
            }
        }
    }

    /// <summary>
    /// Advances a Unix-ms timestamp to the 1st of the next month (UTC).
    /// </summary>
    internal static long AdvanceOneMonth(long unixMs)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        var nextMonth = new DateTime(dt.Year, dt.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(1);
        return new DateTimeOffset(nextMonth).ToUnixTimeMilliseconds();
    }
}
