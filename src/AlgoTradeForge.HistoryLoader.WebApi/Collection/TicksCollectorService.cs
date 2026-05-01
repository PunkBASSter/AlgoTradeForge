using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

/// <summary>
/// Drives periodic backfill + catch-up of the Binance aggregate-trades (ticks) feed
/// for every configured futures asset (TRD §3.5, P2a-1). 5-minute cadence keeps the
/// catch-up window short during normal operation; the per-asset writer's <c>agg_id</c>
/// dedup makes overlapping cycles idempotent.
/// </summary>
internal sealed class TicksCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<TicksCollectorService> logger)
    : ScheduledCollectorService(symbolCollector, circuitBreaker, httpClientFactory, options, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    protected override string ServiceName => nameof(TicksCollectorService);
    protected override string[] CollectedFeedNames => [FeedNames.Ticks];
    protected override string? ScheduleName => "ticks";
}
