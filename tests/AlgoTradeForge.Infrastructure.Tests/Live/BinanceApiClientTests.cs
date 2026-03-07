using System.Text.Json;
using AlgoTradeForge.Infrastructure.Live.Binance;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Live;

public class BinanceApiClientTests
{
    [Fact]
    public void Sign_ProducesValidHmacSha256()
    {
        // Known test vector from Binance API docs
        using var client = new BinanceApiClient(
            "https://api.binance.com",
            "vmPUZE6mv9SD5VNHk4HlWFsOr6aKE2zvsw0MuIgwCIPy6utIco14y7Ju91duEh8A",
            "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j",
            NullLogger.Instance);

        var signature = client.Sign(
            "symbol=LTCBTC&side=BUY&type=LIMIT&timeInForce=GTC&quantity=1&price=0.1&recvWindow=5000&timestamp=1499827319559");

        Assert.Equal("c8db56825ae71d6d79447849e617115f4a920fa2acdcab2b053c4b2838bd6b71", signature);
    }

    [Fact]
    public void Sign_EmptyPayload_ProducesSignature()
    {
        using var client = new BinanceApiClient(
            "https://api.binance.com", "key", "testsecret", NullLogger.Instance);

        var signature = client.Sign("timestamp=1234567890");

        Assert.NotNull(signature);
        Assert.NotEmpty(signature);
        Assert.Equal(64, signature.Length); // SHA-256 produces 64 hex chars
    }

    [Fact]
    public void ExchangeInfo_Deserializes_SymbolInfo()
    {
        var json = """
            {
              "symbols": [
                {
                  "symbol": "BTCUSDT",
                  "baseAsset": "BTC",
                  "quoteAsset": "USDT",
                  "status": "TRADING"
                },
                {
                  "symbol": "ETHUSDT",
                  "baseAsset": "ETH",
                  "quoteAsset": "USDT",
                  "status": "TRADING"
                }
              ]
            }
            """;

        var result = JsonSerializer.Deserialize<BinanceExchangeInfoResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
        })!;

        Assert.Equal(2, result.Symbols.Count);

        var btc = result.Symbols[0];
        Assert.Equal("BTCUSDT", btc.Symbol);
        Assert.Equal("BTC", btc.BaseAsset);
        Assert.Equal("USDT", btc.QuoteAsset);

        var eth = result.Symbols[1];
        Assert.Equal("ETHUSDT", eth.Symbol);
        Assert.Equal("ETH", eth.BaseAsset);
        Assert.Equal("USDT", eth.QuoteAsset);
    }
}
