using System.Text.Json;
using AlgoTradeForge.Infrastructure.Live.Binance;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Live;

public class BinanceMessageParserTests
{
    [Fact]
    public void KlineMessage_Deserializes_Correctly()
    {
        const string json = """
        {
            "e": "kline",
            "E": 1672515782136,
            "s": "BTCUSDT",
            "k": {
                "t": 1672515780000,
                "T": 1672515839999,
                "s": "BTCUSDT",
                "i": "1m",
                "o": "16500.50",
                "h": "16550.00",
                "l": "16490.25",
                "c": "16540.75",
                "v": "1234.567",
                "x": true
            }
        }
        """;

        var msg = JsonSerializer.Deserialize<BinanceKlineMessage>(json, BinanceJsonOptions.Default);

        Assert.NotNull(msg);
        Assert.Equal("kline", msg.EventType);
        Assert.Equal("BTCUSDT", msg.Symbol);
        Assert.True(msg.Kline.IsClosed);
        Assert.Equal("16500.50", msg.Kline.Open);
        Assert.Equal("16550.00", msg.Kline.High);
        Assert.Equal("16490.25", msg.Kline.Low);
        Assert.Equal("16540.75", msg.Kline.Close);
        Assert.Equal("1234.567", msg.Kline.Volume);
        Assert.Equal("1m", msg.Kline.Interval);
        Assert.Equal(1672515780000, msg.Kline.OpenTime);
    }

    [Fact]
    public void KlineMessage_NotClosed_DeserializesCorrectly()
    {
        const string json = """
        {
            "e": "kline",
            "E": 1672515782136,
            "s": "ETHUSDT",
            "k": {
                "t": 1672515780000,
                "T": 1672515839999,
                "s": "ETHUSDT",
                "i": "5m",
                "o": "1200.00",
                "h": "1210.00",
                "l": "1195.00",
                "c": "1205.00",
                "v": "500.0",
                "x": false
            }
        }
        """;

        var msg = JsonSerializer.Deserialize<BinanceKlineMessage>(json, BinanceJsonOptions.Default);

        Assert.NotNull(msg);
        Assert.False(msg.Kline.IsClosed);
    }

    [Fact]
    public void ExecutionReport_Deserializes_Correctly()
    {
        const string json = """
        {
            "e": "executionReport",
            "E": 1672515782136,
            "s": "BTCUSDT",
            "S": "BUY",
            "o": "MARKET",
            "q": "0.001",
            "p": "0.00",
            "L": "16500.50",
            "l": "0.001",
            "z": "0.001",
            "n": "0.00001",
            "i": 12345678,
            "x": "TRADE",
            "X": "FILLED",
            "T": 1672515782000
        }
        """;

        var report = JsonSerializer.Deserialize<BinanceExecutionReport>(json, BinanceJsonOptions.Default);

        Assert.NotNull(report);
        Assert.Equal("executionReport", report.EventType);
        Assert.Equal("BUY", report.Side);
        Assert.Equal("MARKET", report.OrderType);
        Assert.Equal("16500.50", report.LastFilledPrice);
        Assert.Equal("0.001", report.LastFilledQty);
        Assert.Equal(12345678, report.OrderId);
        Assert.Equal("TRADE", report.ExecutionType);
        Assert.Equal("FILLED", report.OrderStatus);
    }

    [Fact]
    public void Price_String_ToInt64_Conversion()
    {
        var tickSize = 0.01m;
        var priceStr = "16500.50";
        var price = decimal.Parse(priceStr);
        var priceScaled = (long)(price / tickSize);

        Assert.Equal(1650050L, priceScaled);
    }

    [Fact]
    public void Price_String_ToInt64_Conversion_SmallTickSize()
    {
        var tickSize = 0.00000001m;
        var priceStr = "0.00012345";
        var price = decimal.Parse(priceStr);
        var priceScaled = (long)(price / tickSize);

        Assert.Equal(12345L, priceScaled);
    }
}
