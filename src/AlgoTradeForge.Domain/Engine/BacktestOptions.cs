namespace AlgoTradeForge.Domain.Engine;

public sealed record BacktestOptions
{
    public required decimal InitialCash { get; init; }
    public decimal CommissionPerTrade { get; init; } = 0m;
    public decimal SlippageTicks { get; init; } = 0m;
    public decimal TickSize { get; init; } = 0.01m;
}
