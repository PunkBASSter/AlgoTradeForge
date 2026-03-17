using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

internal sealed class RatioCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<RatioCollectorService> logger)
    : ScheduledCollectorService(symbolCollector, circuitBreaker, options, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(15);
    protected override string ServiceName => "RatioCollectorService";
    protected override string[] CollectedFeedNames =>
        [FeedNames.LsRatioGlobal, FeedNames.LsRatioTopAccounts, FeedNames.TakerVolume];
}
