using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.WebApi;

namespace AlgoTradeForge.LiveHost.WebApi.Tests;

public class RelayInstrumentSelectorTests
{
    [Fact]
    public void Selects_distinct_tick_asset_names_only()
    {
        var config = new CollectionConfig(
        [
            new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary),
            new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1m")),
            new TickSubscription("ETHUSDT", "binance", DataFeedRole.Primary),
            new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary), // dup
        ]);

        var instruments = RelayInstrumentSelector.StreamableInstruments(config);

        Assert.Equal(["BTCUSDT", "ETHUSDT"], instruments);
    }

    [Fact]
    public void Empty_when_no_tick_feeds()
    {
        var config = new CollectionConfig(
            [new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1m"))]);

        Assert.Empty(RelayInstrumentSelector.StreamableInstruments(config));
    }
}
