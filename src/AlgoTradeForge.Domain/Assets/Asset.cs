using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain;

public abstract record Asset
{
    public required string Name { get; init; }
    public required string Exchange { get; init; }
    public decimal Multiplier { get; init; } = 1m;
    public decimal TickSize { get; init; } = 0.01m;
    public decimal TickValue => TickSize * Multiplier;
    public string Currency { get; init; } = "USD";
    public int DecimalDigits { get; init; } = 2;
    public TimeSpan SmallestInterval { get; init; } = TimeSpan.FromMinutes(1);
    public DateOnly? HistoryStart { get; init; }

    public decimal MinOrderQuantity { get; init; }
    public decimal MaxOrderQuantity { get; init; } = decimal.MaxValue;
    public decimal QuantityStepSize { get; init; }

    public abstract SettlementMode Settlement { get; }

    public virtual long ComputeAutoApplyDelta(AutoApplyType type, double rate, Position position, long lastPrice) => 0L;

    public decimal RoundQuantityDown(decimal quantity)
    {
        if (QuantityStepSize <= 0m) return quantity;
        return Math.Floor(quantity / QuantityStepSize) * QuantityStepSize;
    }

    public override string ToString() => Name;
}
