using System.Collections.Frozen;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class SymbolCollector
{
    private readonly FrozenDictionary<string, IFeedCollector> _collectors;
    private readonly ILogger<SymbolCollector> _logger;

    public SymbolCollector(IEnumerable<IFeedCollector> collectors, ILogger<SymbolCollector> logger)
    {
        _collectors = collectors.ToFrozenDictionary(c => c.FeedName);
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

        // Guard: HTTP 4xx means the symbol may be delisted or restricted — skip gracefully.
        // HTTP 5xx means a transient server error — skip and let the next cycle retry.
        try
        {
            await collector.CollectAsync(assetConfig, feedConfig, assetDir, fromMs, toMs, ct);
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
}
