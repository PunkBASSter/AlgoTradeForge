using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Symbology;

public sealed class BinanceSymbologyTests
{
    private readonly BinanceSymbology _sut = new();

    [Fact]
    public void Exchange_IsBinanceLowercase() =>
        Assert.Equal("binance", _sut.Exchange);

    [Fact]
    public void TryResolve_Spot_ReturnsSpotInstrument()
    {
        var symbol = new CanonicalSymbol("BTC", "USDT", InstrumentKind.Spot, null);

        var ok = _sut.TryResolve(symbol, out var instrument, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.NotNull(instrument);
        Assert.Equal("BTCUSDT",         instrument!.ApiSymbol);
        Assert.Equal(AssetTypes.Spot,   instrument.AssetType);
        Assert.Equal("BTCUSDT",         instrument.Dir);
    }

    [Fact]
    public void TryResolve_Perpetual_ReturnsPerpInstrument()
    {
        var symbol = new CanonicalSymbol("BTC", "USDT", InstrumentKind.Perpetual, null);

        var ok = _sut.TryResolve(symbol, out var instrument, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.NotNull(instrument);
        Assert.Equal("BTCUSDT",              instrument!.ApiSymbol);
        Assert.Equal(AssetTypes.Perpetual,   instrument.AssetType);
        Assert.Equal("BTCUSDT_perp",         instrument.Dir);
    }

    [Fact]
    public void TryResolve_DatedFuture_ReturnsUnsupported()
    {
        var symbol = new CanonicalSymbol("BTC", "USD", InstrumentKind.DatedFuture, "2026-09");

        var ok = _sut.TryResolve(symbol, out var instrument, out var reason);

        Assert.False(ok);
        Assert.Null(instrument);
        Assert.NotNull(reason);
        Assert.Contains("dated futures", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_GetBinance_CaseInsensitive()
    {
        var registry = new SymbologyRegistry([new BinanceSymbology()]);

        Assert.NotNull(registry.Get("binance"));
        Assert.NotNull(registry.Get("Binance"));
        Assert.NotNull(registry.Get("BINANCE"));
    }

    [Fact]
    public void Registry_GetUnknownExchange_ReturnsNull()
    {
        var registry = new SymbologyRegistry([new BinanceSymbology()]);

        Assert.Null(registry.Get("ib"));
    }
}
