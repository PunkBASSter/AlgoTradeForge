using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

/// <summary>
/// Periodic catch-up of the Binance aggregate-trades feed for configured futures assets.
/// 5-minute cadence; the writer's <c>agg_id</c> dedup makes overlapping cycles idempotent.
/// </summary>
internal sealed class TicksCollectorService(
    SymbolCollector symbolCollector,
    ICollectionPlanSource planSource,
    ICollectionCircuitBreaker circuitBreaker,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<TicksCollectorService> logger)
    : ScheduledCollectorService(symbolCollector, planSource, circuitBreaker, httpClientFactory, options, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    protected override string ServiceName => nameof(TicksCollectorService);
    protected override string[] CollectedFeedNames => [FeedNames.Ticks];
    protected override string? ScheduleName => "ticks";
}
