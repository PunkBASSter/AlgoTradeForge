using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Tests.TestData;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

/// <summary>
/// Verifies plan-based resolution and venue-class disambiguation in <see cref="InstrumentAssetDirMap"/>.
/// Both BTCUSDT spot (dir "BTCUSDT") and BTCUSDT perp (dir "BTCUSDT_perp") can coexist in a plan;
/// a futures-named venue must not silently route into the spot directory.
/// </summary>
public sealed class InstrumentAssetDirMapTests
{
    private static CollectionPlan SpotAndPerpPlan(int spotDigits = 2, int perpDigits = 4)
    {
        var spot = CollectionAssets.Spot("BTCUSDT", spotDigits);
        var perp = CollectionAssets.Perp("BTCUSDT", perpDigits);
        return new CollectionPlan([spot, perp], [], []);
    }

    [Fact]
    public void Resolve_FuturesVenue_ReturnsPerpDir_WhenBothSpotAndPerpInPlan()
    {
        var holder = new CollectionPlanHolder();
        holder.Publish(SpotAndPerpPlan());
        var map = new InstrumentAssetDirMap("/data", holder);

        var dir = map.Resolve("binance-futures", "BTCUSDT");

        Assert.Equal(Path.Combine("/data", "binance", "BTCUSDT_perp"), dir);
    }

    [Fact]
    public void Resolve_SpotVenue_ReturnsSpotDir_WhenBothSpotAndPerpInPlan()
    {
        var holder = new CollectionPlanHolder();
        holder.Publish(SpotAndPerpPlan());
        var map = new InstrumentAssetDirMap("/data", holder);

        var dir = map.Resolve("binance", "BTCUSDT");

        Assert.Equal(Path.Combine("/data", "binance", "BTCUSDT"), dir);
    }

    [Fact]
    public void Resolve_InstrumentAbsentFromPlan_FallsBackToVenueSlashInstrument()
    {
        var map = new InstrumentAssetDirMap("/data", new CollectionPlanHolder());

        var dir = map.Resolve("binance-futures", "XYZUSDT");

        Assert.Equal(Path.Combine("/data", "binance-futures", "XYZUSDT"), dir);
    }

    [Fact]
    public void ResolveDigits_FuturesVenue_ReturnsDigitsFromPerpAsset()
    {
        var holder = new CollectionPlanHolder();
        holder.Publish(SpotAndPerpPlan(spotDigits: 2, perpDigits: 4));
        var map = new InstrumentAssetDirMap("/data", holder);

        var digits = map.ResolveDigits("binance-futures", "BTCUSDT");

        Assert.Equal(4, digits);
    }

    [Fact]
    public void ResolveDigits_SpotVenue_ReturnsDigitsFromSpotAsset()
    {
        var holder = new CollectionPlanHolder();
        holder.Publish(SpotAndPerpPlan(spotDigits: 2, perpDigits: 4));
        var map = new InstrumentAssetDirMap("/data", holder);

        var digits = map.ResolveDigits("binance", "BTCUSDT");

        Assert.Equal(2, digits);
    }

    [Fact]
    public void ResolveDigits_InstrumentAbsentFromPlan_ReturnsNull()
    {
        var map = new InstrumentAssetDirMap("/data", new CollectionPlanHolder());

        var digits = map.ResolveDigits("binance", "XYZUSDT");

        Assert.Null(digits);
    }
}
