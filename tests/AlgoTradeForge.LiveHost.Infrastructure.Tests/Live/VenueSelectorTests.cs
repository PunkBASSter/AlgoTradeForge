using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class VenueSelectorTests
{
    [Theory]
    [InlineData("ib", VenueKind.Ib)]
    [InlineData("IB", VenueKind.Ib)]
    [InlineData("binance", VenueKind.Binance)]
    [InlineData(null, VenueKind.Binance)]
    [InlineData("", VenueKind.Binance)]
    public void Parse_MapsKnownVenues(string? input, VenueKind expected) =>
        Assert.Equal(expected, VenueSelector.Parse(input));

    [Fact]
    public void Parse_UnknownVenue_Throws() =>
        Assert.Throws<ArgumentException>(() => VenueSelector.Parse("kraken"));
}
