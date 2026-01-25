using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Tests.TestUtilities;

public static class TestBars
{
    private static readonly DateTimeOffset DefaultTimestamp = new(2024, 1, 1, 9, 30, 0, TimeSpan.Zero);

    public static OhlcvBar Create(
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume = 1000m,
        DateTimeOffset? timestamp = null) =>
        new(timestamp ?? DefaultTimestamp, open, high, low, close, volume);

    public static OhlcvBar Bullish(DateTimeOffset? timestamp = null) =>
        Create(100m, 110m, 98m, 108m, timestamp: timestamp);

    public static OhlcvBar Bearish(DateTimeOffset? timestamp = null) =>
        Create(100m, 102m, 90m, 92m, timestamp: timestamp);

    public static OhlcvBar Flat(DateTimeOffset? timestamp = null) =>
        Create(100m, 101m, 99m, 100m, timestamp: timestamp);

    public static OhlcvBar AtPrice(decimal price, DateTimeOffset? timestamp = null) =>
        Create(price, price + 1m, price - 1m, price, timestamp: timestamp);

    public static OhlcvBar[] CreateSequence(int count, decimal startPrice = 100m, decimal priceIncrement = 1m)
    {
        var bars = new OhlcvBar[count];
        var timestamp = DefaultTimestamp;

        for (var i = 0; i < count; i++)
        {
            var price = startPrice + i * priceIncrement;
            bars[i] = Create(price, price + 2m, price - 1m, price + 1m, timestamp: timestamp);
            timestamp = timestamp.AddMinutes(1);
        }

        return bars;
    }
}
