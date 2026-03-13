using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain;

public abstract record Asset
{
    public required string Name { get; init; }
    public required string Exchange { get; init; }
    public abstract decimal Multiplier { get; init; }
    public decimal TickSize { get; init; } = 0.01m;

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
