using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Collection;

internal sealed class OiCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<OiCollectorService> logger)
    : ScheduledCollectorService(symbolCollector, circuitBreaker, options, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    protected override string ServiceName => "OiCollectorService";
    protected override string[] CollectedFeedNames => [FeedNames.OpenInterest];
}
