using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain;

public sealed record FutureAsset : Asset, IMarginAsset
{
    public override decimal Multiplier { get; init; }
    public override SettlementMode Settlement => SettlementMode.Margin;
    public decimal? MarginRequirement { get; init; }

    public override long ComputeAutoApplyDelta(AutoApplyType type, double rate, Position position, long lastPrice)
    {
        if (position.Quantity == 0m) return 0L;

        return type switch
        {
            AutoApplyType.SwapRate => MoneyConvert.ToLong(
                -position.Quantity * (decimal)lastPrice * (decimal)rate * Multiplier / 365m),
            _ => 0L,
        };
    }

    public static FutureAsset Create(string name, string exchange, decimal multiplier, decimal tickSize,
        decimal? margin = null,
        decimal minOrderQuantity = 1m, decimal maxOrderQuantity = decimal.MaxValue, decimal quantityStepSize = 1m) =>
        new()
        {
            Name = name,
            Exchange = exchange,
            Multiplier = multiplier,
            TickSize = tickSize,
            MarginRequirement = margin,
            MinOrderQuantity = minOrderQuantity,
            MaxOrderQuantity = maxOrderQuantity,
            QuantityStepSize = quantityStepSize,
        };
}
