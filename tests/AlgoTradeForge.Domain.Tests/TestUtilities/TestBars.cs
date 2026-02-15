using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Tests.TestUtilities;

public static class TestBars
{
    private static readonly DateTimeOffset DefaultTimestamp = new(2024, 1, 1, 9, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan DefaultStep = TimeSpan.FromMinutes(1);

    public static Int64Bar Create(
        long open,
        long high,
        long low,
        long close,
        long volume = 1000L) =>
        new(open, high, low, close, volume);

    public static Int64Bar Bullish() =>
        Create(10000, 11000, 9800, 10800);

    public static Int64Bar Bearish() =>
        Create(10000, 10200, 9000, 9200);

    public static Int64Bar Flat() =>
        Create(10000, 10100, 9900, 10000);

    public static Int64Bar AtPrice(long price) =>
        Create(price, price + 100, price - 100, price);

    public static TimeSeries<Int64Bar> CreateSeries(int count, long startPrice = 10000, long priceIncrement = 100)
    {
        var series = new TimeSeries<Int64Bar>(DefaultTimestamp, DefaultStep);

        for (var i = 0; i < count; i++)
        {
            var price = startPrice + i * priceIncrement;
            series.Add(Create(price, price + 200, price - 100, price + 100));
        }

        return series;
    }

    public static TimeSeries<Int64Bar> CreateSeries(params Int64Bar[] bars)
    {
        var series = new TimeSeries<Int64Bar>(DefaultTimestamp, DefaultStep);
        foreach (var bar in bars)
            series.Add(bar);
        return series;
    }

    public static TimeSeries<Int64Bar> CreateSeries(
        DateTimeOffset startTime,
        TimeSpan step,
        int count,
        long startPrice = 10000,
        long priceIncrement = 100)
    {
        var series = new TimeSeries<Int64Bar>(startTime, step);
        for (var i = 0; i < count; i++)
        {
            var price = startPrice + i * priceIncrement;
            series.Add(Create(price, price + 200, price - 100, price + 100));
        }
        return series;
    }

    public static TimeSeries<Int64Bar> CreateSeries(
        DateTimeOffset startTime,
        TimeSpan step,
        params Int64Bar[] bars)
    {
        var series = new TimeSeries<Int64Bar>(startTime, step);
        foreach (var bar in bars)
            series.Add(bar);
        return series;
    }
}
