using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;

namespace AlgoTradeForge.LiveHost.WebApi;

/// <summary>Projects the collection config to the relay's streamable instrument list.</summary>
public static class RelayInstrumentSelector
{
    // Today only Tick feeds are streamed by the relay (trades). book-ticker/quotes are future.
    public static string[] StreamableInstruments(CollectionConfig config) =>
        config.Feeds
            .OfType<TickSubscription>()
            .Select(t => t.AssetName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
