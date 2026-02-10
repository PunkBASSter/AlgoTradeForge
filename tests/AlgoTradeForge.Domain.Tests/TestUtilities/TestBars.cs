using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Tests.TestUtilities;

public static class TestBars
{
    private static readonly DateTimeOffset DefaultTimestamp = new(2024, 1, 1, 9, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan DefaultStep = TimeSpan.FromMinutes(1);

    public static IntBar Create(
        long open,
        long high,
        long low,
        long close,
        long volume = 1000L) =>
        new(open, high, low, close, volume);

    public static IntBar Bullish() =>
        Create(10000, 11000, 9800, 10800);

    public static IntBar Bearish() =>
        Create(10000, 10200, 9000, 9200);

    public static IntBar Flat() =>
        Create(10000, 10100, 9900, 10000);

    public static IntBar AtPrice(long price) =>
        Create(price, price + 100, price - 100, price);

    public static TimeSeries<IntBar> CreateSeries(int count, long startPrice = 10000, long priceIncrement = 100)
    {
        var series = new TimeSeries<IntBar>(DefaultTimestamp, DefaultStep);

        for (var i = 0; i < count; i++)
        {
            var price = startPrice + i * priceIncrement;
            series.Add(Create(price, price + 200, price - 100, price + 100));
        }

        return series;
    }

    public static TimeSeries<IntBar> CreateSeries(params IntBar[] bars)
    {
        var series = new TimeSeries<IntBar>(DefaultTimestamp, DefaultStep);
        foreach (var bar in bars)
            series.Add(bar);
        return series;
    }
}
