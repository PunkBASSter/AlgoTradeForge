using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

internal sealed class LiquidationCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<LiquidationCollectorService> logger)
    : ScheduledCollectorService(symbolCollector, circuitBreaker, options, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromHours(4);
    protected override string ServiceName => "LiquidationCollectorService";
    protected override string[] CollectedFeedNames => [FeedNames.Liquidations];
}
