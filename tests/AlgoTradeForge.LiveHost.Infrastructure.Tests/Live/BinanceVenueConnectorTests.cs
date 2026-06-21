using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class BinanceVenueConnectorTests
{
    [Fact]
    public void MapsAggTradeToCanonicalTradeEvent()
    {
        // priceExp=2 (tick 0.01 → ×100), qtyExp=5 (step 0.00001 → ×100000)
        // Using DIFFERENT exponents so a swapped-scale bug would fail the quantity assertion.
        var scale = new TickScale(PriceExp: 2, QtyExp: 5);
        var dto = new BinanceAggTrade(
            EventTimeMs: 1_700_000_000_001L,
            AggId: 42L,
            Price: "50000.00",
            Quantity: "1.23456",
            IsBuyerMaker: true);

        var ev = BinanceVenueConnector.ToTradeEvent("BTCUSDT", dto, scale);

        Assert.Equal("BTCUSDT", ev.Instrument);
        Assert.Equal(1_700_000_000_001L, ev.Tick.TimestampMs);
        // price: 50000.00 × 10^2 = 5_000_000
        Assert.Equal(5_000_000L, ev.Tick.Price);
        // qty:   1.23456 × 10^5  = 123_456
        Assert.Equal(123_456L, ev.Tick.Quantity);
        Assert.Equal(42L, ev.Tick.Sequence);
        // buyer is maker ⇒ aggressor is the seller
        Assert.Equal(AggressorSide.Sell, ev.Tick.Aggressor);
    }

    [Fact]
    public void MapsAggTrade_BuyerIsTaker_AggressorIsBuy()
    {
        var scale = new TickScale(PriceExp: 2, QtyExp: 5);
        var dto = new BinanceAggTrade(
            EventTimeMs: 1_700_000_000_002L,
            AggId: 99L,
            Price: "30000.50",
            Quantity: "0.50000",
            IsBuyerMaker: false);

        var ev = BinanceVenueConnector.ToTradeEvent("BTCUSDT", dto, scale);

        // buyer is taker ⇒ aggressor is the buyer
        Assert.Equal(AggressorSide.Buy, ev.Tick.Aggressor);
        // price: 30000.50 × 100 = 3_000_050
        Assert.Equal(3_000_050L, ev.Tick.Price);
        // qty: 0.50000 × 100000 = 50_000
        Assert.Equal(50_000L, ev.Tick.Quantity);
    }
}
