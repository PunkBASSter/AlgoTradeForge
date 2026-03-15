using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.HistoryLoader.Infrastructure;

internal sealed class FeedFetcherFactory(IServiceProvider serviceProvider) : IFeedFetcherFactory
{
    public IFeedFetcher Create(string exchangeKey, string feedName)
        => serviceProvider.GetRequiredKeyedService<IFeedFetcher>($"{exchangeKey}:{feedName}");
}
