namespace AlgoTradeForge.Domain;

public readonly record struct ScaleContext
{
    public decimal TickSize { get; }
    internal decimal ScaleFactor { get; }

    public ScaleContext(Asset asset)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(asset.TickSize);
        TickSize = asset.TickSize;
        ScaleFactor = 1m / asset.TickSize;
    }

    public ScaleContext(decimal tickSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickSize);
        TickSize = tickSize;
        ScaleFactor = 1m / tickSize;
    }

    public long AmountToTicks(decimal value) => MoneyConvert.ToLong(value * ScaleFactor);
    public decimal TicksToAmount(long ticks) => ticks * TickSize;
    public decimal TicksToAmount(decimal ticks) => ticks * TickSize;
    public long FromMarketPrice(decimal price) => MoneyConvert.ToLong(price / TickSize);
    public decimal ToMarketPrice(long ticks) => ticks * TickSize;
}
