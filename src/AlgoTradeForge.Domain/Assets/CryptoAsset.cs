using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain;

public sealed record CryptoAsset : Asset
{
    public override ISettlementCalculator SettlementCalculator => CashAndCarrySettlement.Instance;

    public static CryptoAsset Create(string name, string exchange, int decimalDigits,
        DateOnly? historyStart = null,
        decimal minOrderQuantity = 0m, decimal maxOrderQuantity = decimal.MaxValue, decimal quantityStepSize = 0m,
        decimal shortMarginRate = 1.0m) =>
        new()
        {
            Name = name,
            Exchange = exchange,
            DecimalDigits = decimalDigits,
            HistoryStart = historyStart,
            TickSize = 1m / (decimal)Math.Pow(10, decimalDigits),
            MinOrderQuantity = minOrderQuantity,
            MaxOrderQuantity = maxOrderQuantity,
            QuantityStepSize = quantityStepSize,
            ShortMarginRate = shortMarginRate,
        };
}
