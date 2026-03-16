using System.Text;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class LiquidationStreamServiceTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static byte[] BuildForceOrderJson(
        string symbol = "BTCUSDT",
        string side = "SELL",
        string status = "FILLED",
        string avgPrice = "9910.00",
        string execQty = "0.014",
        long tradeTime = 1_700_000_000_000L,
        string eventType = "forceOrder")
    {
        var json = $@"{{
            ""e"": ""{eventType}"",
            ""E"": {tradeTime},
            ""o"": {{
                ""s"": ""{symbol}"",
                ""S"": ""{side}"",
                ""o"": ""LIMIT"",
                ""f"": ""IOC"",
                ""q"": ""0.100"",
                ""p"": ""9900.00"",
                ""ap"": ""{avgPrice}"",
                ""X"": ""{status}"",
                ""l"": ""{execQty}"",
                ""z"": ""{execQty}"",
                ""T"": {tradeTime}
            }}
        }}";
        return Encoding.UTF8.GetBytes(json);
    }

    // -------------------------------------------------------------------------
    // 1. ParseForceOrder_SellFilled_ReturnsLongLiquidated
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseForceOrder_SellFilled_ReturnsLongLiquidated()
    {
        // SELL = long position liquidated → side = 1.0
        var data = BuildForceOrderJson(
            symbol: "BTCUSDT",
            side: "SELL",
            avgPrice: "9910.00",
            execQty: "0.014");

        var result = LiquidationStreamService.ParseForceOrder(data);

        Assert.NotNull(result);
        var (symbol, record) = result.Value;
        Assert.Equal("BTCUSDT", symbol);
        Assert.Equal(1_700_000_000_000L, record.TimestampMs);
        Assert.Equal(4, record.Values.Length);
        Assert.Equal(1.0,    record.Values[0], precision: 10);   // side: long liquidated
        Assert.Equal(9910.0, record.Values[1], precision: 5);    // avgPrice
        Assert.Equal(0.014,  record.Values[2], precision: 10);   // execQty
        Assert.Equal(138.74, record.Values[3], precision: 2);    // notional = 0.014 * 9910
    }

    // -------------------------------------------------------------------------
    // 2. ParseForceOrder_BuyFilled_ReturnsShortLiquidated
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseForceOrder_BuyFilled_ReturnsShortLiquidated()
    {
        // BUY = short position liquidated → side = -1.0
        var data = BuildForceOrderJson(
            side: "BUY",
            avgPrice: "31050.00",
            execQty: "2.000");

        var result = LiquidationStreamService.ParseForceOrder(data);

        Assert.NotNull(result);
        var (_, record) = result.Value;
        Assert.Equal(-1.0,    record.Values[0], precision: 10);  // side: short liquidated
        Assert.Equal(31050.0, record.Values[1], precision: 5);
        Assert.Equal(2.0,     record.Values[2], precision: 10);
        Assert.Equal(62100.0, record.Values[3], precision: 5);   // notional = 2.0 * 31050
    }

    // -------------------------------------------------------------------------
    // 3. ParseForceOrder_PartiallyFilled_ReturnsNull
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseForceOrder_PartiallyFilled_ReturnsNull()
    {
        var data = BuildForceOrderJson(status: "PARTIALLY_FILLED");

        var result = LiquidationStreamService.ParseForceOrder(data);

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // 4. ParseForceOrder_WrongEventType_ReturnsNull
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseForceOrder_WrongEventType_ReturnsNull()
    {
        var data = BuildForceOrderJson(eventType: "markPriceUpdate");

        var result = LiquidationStreamService.ParseForceOrder(data);

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // 5. ParseForceOrder_MissingField_ReturnsNull
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseForceOrder_MissingField_ReturnsNull()
    {
        // JSON without "ap" property
        var json = @"{
            ""e"": ""forceOrder"",
            ""E"": 1700000000000,
            ""o"": {
                ""s"": ""BTCUSDT"",
                ""S"": ""SELL"",
                ""X"": ""FILLED"",
                ""z"": ""0.014"",
                ""T"": 1700000000000
            }
        }";
        var data = Encoding.UTF8.GetBytes(json);

        var result = LiquidationStreamService.ParseForceOrder(data);

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // 6. ParseForceOrder_InvalidPrice_ReturnsNull
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseForceOrder_InvalidPrice_ReturnsNull()
    {
        var data = BuildForceOrderJson(avgPrice: "abc");

        var result = LiquidationStreamService.ParseForceOrder(data);

        Assert.Null(result);
    }
}
