using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain;

public sealed record Asset
{
    public required string Name { get; init; }
    public AssetType Type { get; init; } = AssetType.Equity;
    public SettlementModel Settlement { get; init; } = SettlementModel.CashAndCarry;
    public decimal Multiplier { get; init; } = 1m;
    public decimal TickSize { get; init; } = 0.01m;
    public decimal TickValue => TickSize * Multiplier;
    public string Currency { get; init; } = "USD";
    public decimal? MarginRequirement { get; init; }
    public decimal ShortMarginRate { get; init; } = 1.0m;
    public required string Exchange { get; init; }
    public int DecimalDigits { get; init; } = 2;
    public TimeSpan SmallestInterval { get; init; } = TimeSpan.FromMinutes(1);
    public DateOnly? HistoryStart { get; init; }

    public decimal MinOrderQuantity { get; init; }
    public decimal MaxOrderQuantity { get; init; } = decimal.MaxValue;
    public decimal QuantityStepSize { get; init; }

    public decimal RoundQuantityDown(decimal quantity)
    {
        if (QuantityStepSize <= 0m) return quantity;
        return Math.Floor(quantity / QuantityStepSize) * QuantityStepSize;
    }

    public static Asset Equity(string name, string exchange,
        decimal minOrderQuantity = 1m, decimal maxOrderQuantity = decimal.MaxValue, decimal quantityStepSize = 1m,
        decimal shortMarginRate = 1.0m) =>
        new()
        {
            Name = name,
            Exchange = exchange,
            MinOrderQuantity = minOrderQuantity,
            MaxOrderQuantity = maxOrderQuantity,
            QuantityStepSize = quantityStepSize,
            ShortMarginRate = shortMarginRate
        };

    public static Asset Future(string name, string exchange, decimal multiplier, decimal tickSize, decimal? margin = null,
        decimal minOrderQuantity = 1m, decimal maxOrderQuantity = decimal.MaxValue, decimal quantityStepSize = 1m,
        decimal shortMarginRate = 1.0m) =>
        new()
        {
            Name = name,
            Type = AssetType.Future,
            Settlement = SettlementModel.Margin,
            Exchange = exchange,
            Multiplier = multiplier,
            TickSize = tickSize,
            MarginRequirement = margin,
            MinOrderQuantity = minOrderQuantity,
            MaxOrderQuantity = maxOrderQuantity,
            QuantityStepSize = quantityStepSize,
            ShortMarginRate = shortMarginRate
        };

    public static Asset CryptoPerpetual(string name, string exchange, int decimalDigits, decimal? margin = null,
        DateOnly? historyStart = null,
        decimal minOrderQuantity = 0m, decimal maxOrderQuantity = decimal.MaxValue, decimal quantityStepSize = 0m) =>
        new()
        {
            Name = name,
            Type = AssetType.CryptoPerpetual,
            Settlement = SettlementModel.Margin,
            Exchange = exchange,
            DecimalDigits = decimalDigits,
            HistoryStart = historyStart,
            TickSize = 1m / (decimal)Math.Pow(10, decimalDigits),
            MarginRequirement = margin,
            MinOrderQuantity = minOrderQuantity,
            MaxOrderQuantity = maxOrderQuantity,
            QuantityStepSize = quantityStepSize,
        };

    public static Asset StockFuture(string name, string exchange, decimal multiplier, decimal tickSize,
        decimal? margin = null,
        decimal minOrderQuantity = 1m, decimal maxOrderQuantity = decimal.MaxValue, decimal quantityStepSize = 1m) =>
        Future(name, exchange, multiplier, tickSize, margin, minOrderQuantity, maxOrderQuantity, quantityStepSize);

    public static Asset IndexFuture(string name, string exchange, decimal multiplier, decimal tickSize,
        decimal? margin = null,
        decimal minOrderQuantity = 1m, decimal maxOrderQuantity = decimal.MaxValue, decimal quantityStepSize = 1m) =>
        Future(name, exchange, multiplier, tickSize, margin, minOrderQuantity, maxOrderQuantity, quantityStepSize);

    public static Asset Crypto(string name, string exchange, int decimalDigits, DateOnly? historyStart = null,
        decimal minOrderQuantity = 0m, decimal maxOrderQuantity = decimal.MaxValue, decimal quantityStepSize = 0m,
        decimal shortMarginRate = 1.0m) =>
        new()
        {
            Name = name,
            Type = AssetType.Crypto,
            Exchange = exchange,
            DecimalDigits = decimalDigits,
            HistoryStart = historyStart,
            TickSize = 1m / (decimal)Math.Pow(10, decimalDigits),
            MinOrderQuantity = minOrderQuantity,
            MaxOrderQuantity = maxOrderQuantity,
            QuantityStepSize = quantityStepSize,
            ShortMarginRate = shortMarginRate
        };

    public override string ToString() => Name;
}
