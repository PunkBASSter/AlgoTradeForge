using AlgoTradeForge.HistoryLoader.Domain;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Catalog;

public class AssetDirectoryClassifierTests
{
    [Theory]
    [InlineData("NASDAQ", "AAPL", "AAPL", AssetTypes.Equity)]
    [InlineData("NYSE", "SPY", "SPY", AssetTypes.Equity)]
    [InlineData("binance", "BTCUSDT", "BTCUSDT", AssetTypes.Spot)]
    [InlineData("binance", "BTCUSDT_perp", "BTCUSDT", AssetTypes.Perpetual)]
    [InlineData("NASDAQ", "AAPL_perp", "AAPL", AssetTypes.Perpetual)] // _perp wins over equity-exchange
    public void Classify_maps_exchange_and_suffix(string exchange, string dir, string expectedSymbol, string expectedType)
    {
        var (symbol, type) = AssetDirectoryClassifier.Classify(exchange, dir);
        Assert.Equal(expectedSymbol, symbol);
        Assert.Equal(expectedType, type);
    }

    [Fact]
    public void IsUsEquityExchange_is_case_insensitive()
    {
        Assert.True(AssetDirectoryClassifier.IsUsEquityExchange("nasdaq"));
        Assert.False(AssetDirectoryClassifier.IsUsEquityExchange("binance"));
    }
}
