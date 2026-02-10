namespace AlgoTradeForge.Domain;

public sealed record Asset
{
    public required string Name { get; init; }
    public AssetType Type { get; init; } = AssetType.Equity;
    public decimal Multiplier { get; init; } = 1m;
    public decimal TickSize { get; init; } = 0.01m;
    public decimal TickValue => TickSize * Multiplier;
    public string Currency { get; init; } = "USD";
    public decimal? MarginRequirement { get; init; }
    public string? Exchange { get; init; }
    public int DecimalDigits { get; init; } = 2;
    public TimeSpan SmallestInterval { get; init; } = TimeSpan.FromMinutes(1);
    public DateOnly? HistoryStart { get; init; }

    public static Asset Equity(string name) => new() { Name = name };

    public static Asset Future(string name, decimal multiplier, decimal tickSize, decimal? margin = null) =>
        new()
        {
            Name = name,
            Type = AssetType.Future,
            Multiplier = multiplier,
            TickSize = tickSize,
            MarginRequirement = margin
        };

    public static Asset Crypto(string name, string exchange, int decimalDigits, DateOnly? historyStart = null) =>
        new()
        {
            Name = name,
            Type = AssetType.Crypto,
            Exchange = exchange,
            DecimalDigits = decimalDigits,
            HistoryStart = historyStart,
            TickSize = 1m / (decimal)Math.Pow(10, decimalDigits)
        };

    public override string ToString() => Name;
}
