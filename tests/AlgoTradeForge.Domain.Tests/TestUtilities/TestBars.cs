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
        long volume = 1000L,
        long timestampMs = 0L) =>
        new(timestampMs, open, high, low, close, volume);

    public static Int64Bar Bullish(long timestampMs = 0L) =>
        Create(10000, 11000, 9800, 10800, timestampMs: timestampMs);

    public static Int64Bar Bearish(long timestampMs = 0L) =>
        Create(10000, 10200, 9000, 9200, timestampMs: timestampMs);

    public static Int64Bar Flat(long timestampMs = 0L) =>
        Create(10000, 10100, 9900, 10000, timestampMs: timestampMs);

    public static Int64Bar AtPrice(long price, long timestampMs = 0L) =>
        Create(price, price + 100, price - 100, price, timestampMs: timestampMs);

    public static TimeSeries<Int64Bar> CreateSeries(int count, long startPrice = 10000, long priceIncrement = 100)
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = DefaultTimestamp.ToUnixTimeMilliseconds();
        var stepMs = (long)DefaultStep.TotalMilliseconds;

        for (var i = 0; i < count; i++)
        {
            var price = startPrice + i * priceIncrement;
            series.Add(Create(price, price + 200, price - 100, price + 100, timestampMs: startMs + i * stepMs));
        }

        return series;
    }

    public static TimeSeries<Int64Bar> CreateSeries(params Int64Bar[] bars)
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = DefaultTimestamp.ToUnixTimeMilliseconds();
        var stepMs = (long)DefaultStep.TotalMilliseconds;

        for (var i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar.TimestampMs == 0L)
                bar = bar with { TimestampMs = startMs + i * stepMs };
            series.Add(bar);
        }

        return series;
    }

    public static TimeSeries<Int64Bar> CreateSeries(
        DateTimeOffset startTime,
        TimeSpan step,
        int count,
        long startPrice = 10000,
        long priceIncrement = 100)
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = startTime.ToUnixTimeMilliseconds();
        var stepMs = (long)step.TotalMilliseconds;

        for (var i = 0; i < count; i++)
        {
            var price = startPrice + i * priceIncrement;
            series.Add(Create(price, price + 200, price - 100, price + 100, timestampMs: startMs + i * stepMs));
        }

        return series;
    }

    public static TimeSeries<Int64Bar> CreateSeries(
        DateTimeOffset startTime,
        TimeSpan step,
        params Int64Bar[] bars)
    {
        var series = new TimeSeries<Int64Bar>();
        var startMs = startTime.ToUnixTimeMilliseconds();
        var stepMs = (long)step.TotalMilliseconds;

        for (var i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar.TimestampMs == 0L)
                bar = bar with { TimestampMs = startMs + i * stepMs };
            series.Add(bar);
        }

        return series;
    }
}
