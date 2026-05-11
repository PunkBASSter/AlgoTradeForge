namespace AlgoTradeForge.Domain;

public readonly record struct ScaleContext
{
    public decimal TickSize { get; }

    /// <summary>
    /// Multiplier converting raw base-asset quantities (e.g. tick <c>qty</c>) to tick-scaled
    /// <see cref="long"/> at the aggregator's sum site. Distinct from <see cref="TickSize"/>
    /// (price-tick scale) and from order-size handling (which stays <see cref="decimal"/>).
    /// Equals <c>1 / asset.QuantityStepSize</c> when set, otherwise <c>1</c> (identity).
    /// </summary>
    public decimal QuantityScale { get; }

    internal decimal ScaleFactor { get; }

    public ScaleContext(Asset asset)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(asset.TickSize);
        TickSize = asset.TickSize;
        ScaleFactor = 1m / asset.TickSize;
        QuantityScale = asset.QuantityStepSize > 0m ? 1m / asset.QuantityStepSize : 1m;
    }

    public ScaleContext(decimal tickSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickSize);
        TickSize = tickSize;
        ScaleFactor = 1m / tickSize;
        QuantityScale = 1m;
    }

    public ScaleContext(decimal tickSize, decimal quantityStepSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickSize);
        ArgumentOutOfRangeException.ThrowIfNegative(quantityStepSize);
        TickSize = tickSize;
        ScaleFactor = 1m / tickSize;
        QuantityScale = quantityStepSize > 0m ? 1m / quantityStepSize : 1m;
    }

    public long AmountToTicks(decimal value) => MoneyConvert.ToLong(value * ScaleFactor);
    public decimal TicksToAmount(long ticks) => ticks * TickSize;
    public decimal TicksToAmount(decimal ticks) => ticks * TickSize;
    public long FromMarketPrice(decimal price) => MoneyConvert.ToLong(price / TickSize);
    public decimal ToMarketPrice(long ticks) => ticks * TickSize;
}
