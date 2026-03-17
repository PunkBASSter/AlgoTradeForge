using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

internal sealed class FundingRateCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<FundingRateCollectorService> logger)
    : ScheduledCollectorService(symbolCollector, circuitBreaker, options, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromHours(8);
    protected override string ServiceName => "FundingRateCollectorService";
    protected override string[] CollectedFeedNames => [FeedNames.FundingRate];
    protected override string? ScheduleName => "binance-funding-rate";
}
