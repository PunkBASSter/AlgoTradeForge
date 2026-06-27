using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class InstrumentScaleMapTests
{
    [Fact]
    public void Build_KeysByAssetName_WithEachInstrumentsOwnScale()
    {
        var btc = CryptoAsset.Create("BTCUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);
        var eth = CryptoAsset.Create("ETHUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.0001m, maxOrderQuantity: 9000m, quantityStepSize: 0.0001m);

        var subs = new List<DataFeedSubscription>
        {
            new TickSubscription("BTCUSDT", "Binance", DataFeedRole.Primary) { Asset = btc },
            new TickSubscription("ETHUSDT", "Binance", DataFeedRole.Primary) { Asset = eth },
        };

        var map = InstrumentScaleMap.Build(subs);

        Assert.Equal(new ScaleContext(btc).TickSize, map["BTCUSDT"].TickSize);
        Assert.Equal(new ScaleContext(eth).TickSize, map["ETHUSDT"].TickSize);
    }
}
