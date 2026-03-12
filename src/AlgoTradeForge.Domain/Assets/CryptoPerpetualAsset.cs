using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain;

public sealed record CryptoPerpetualAsset : Asset, IMarginAsset
{
    public override SettlementMode Settlement => SettlementMode.Margin;
    public decimal? MarginRequirement { get; init; }

    public override long ComputeAutoApplyDelta(AutoApplyType type, double rate, Position position, long lastPrice)
    {
        if (position.Quantity == 0m) return 0L;

        return type switch
        {
            AutoApplyType.FundingRate => MoneyConvert.ToLong(
                -position.Quantity * (decimal)lastPrice * (decimal)rate * Multiplier),
            AutoApplyType.SwapRate => MoneyConvert.ToLong(
                -position.Quantity * (decimal)lastPrice * (decimal)rate * Multiplier / 365m),
            _ => 0L,
        };
    }

    public static CryptoPerpetualAsset Create(string name, string exchange, int decimalDigits,
        decimal? margin = null, DateOnly? historyStart = null,
        decimal minOrderQuantity = 0m, decimal maxOrderQuantity = decimal.MaxValue, decimal quantityStepSize = 0m) =>
        new()
        {
            Name = name,
            Exchange = exchange,
            DecimalDigits = decimalDigits,
            HistoryStart = historyStart,
            TickSize = 1m / (decimal)Math.Pow(10, decimalDigits),
            MarginRequirement = margin,
            MinOrderQuantity = minOrderQuantity,
            MaxOrderQuantity = maxOrderQuantity,
            QuantityStepSize = quantityStepSize,
        };
}
