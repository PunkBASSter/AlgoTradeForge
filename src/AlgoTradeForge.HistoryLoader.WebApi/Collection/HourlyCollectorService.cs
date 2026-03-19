using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

internal sealed class HourlyCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<HourlyCollectorService> logger)
    : ScheduledCollectorService(symbolCollector, circuitBreaker, httpClientFactory, options, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromHours(1);
    protected override string ServiceName => "HourlyCollectorService";
    protected override string[] CollectedFeedNames =>
        [FeedNames.MarkPrice, FeedNames.LsRatioTopPositions];
}
