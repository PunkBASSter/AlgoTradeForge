using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Symbology;

public sealed class CanonicalSymbolParserTests
{
    [Theory]
    [InlineData("BTC/USDT",             "BTC", "USDT", InstrumentKind.Spot,        null)]
    [InlineData("BTC/USDT-PERP",        "BTC", "USDT", InstrumentKind.Perpetual,   null)]
    [InlineData("BTC/USD-FUT-2026-09",  "BTC", "USD",  InstrumentKind.DatedFuture, "2026-09")]
    public void TryParse_ValidInput_ParsesAndRoundTrips(
        string input, string expectedBase, string expectedQuote,
        InstrumentKind expectedKind, string? expectedExpiry)
    {
        var ok = CanonicalSymbolParser.TryParse(input, out var symbol, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(symbol);
        Assert.Equal(expectedBase,   symbol!.Base);
        Assert.Equal(expectedQuote,  symbol.Quote);
        Assert.Equal(expectedKind,   symbol.Kind);
        Assert.Equal(expectedExpiry, symbol.Expiry);
        Assert.Equal(input,          symbol.ToString());
    }

    [Theory]
    [InlineData("btc/usdt")]
    [InlineData("BTC-USDT")]
    [InlineData("BTC/USDT-PERP-X")]
    [InlineData("BTC/USD-FUT-2026-13")]
    [InlineData("BTC/USD-FUT-202X-09")]
    [InlineData("")]
    [InlineData("/USDT")]
    [InlineData("BTC/")]
    [InlineData("BTC/USD-FUT-1999-09")]
    [InlineData("BTCUSDTLONGSYMBOLMORE123/USDT")]
    public void TryParse_InvalidInput_ReturnsFalseWithError(string input)
    {
        var ok = CanonicalSymbolParser.TryParse(input, out var symbol, out var error);

        Assert.False(ok);
        Assert.Null(symbol);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_OptionsInstrument_ReturnsReservedError()
    {
        var ok = CanonicalSymbolParser.TryParse("BTC/USD-OPT-2026-09-60000-C", out var symbol, out var error);

        Assert.False(ok);
        Assert.Null(symbol);
        Assert.Equal("options instruments are reserved, not yet supported", error);
    }
}
