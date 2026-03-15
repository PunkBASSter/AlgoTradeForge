using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.HistoryLoader.Infrastructure;

internal sealed class CandleFetcherFactory(IServiceProvider serviceProvider) : ICandleFetcherFactory
{
    public ICandleFetcher Create(string exchangeKey)
        => serviceProvider.GetRequiredKeyedService<ICandleFetcher>(exchangeKey);
}
