namespace AlgoTradeForge.Application;

public sealed record BacktestSettingsDto
{
    public required decimal InitialCash { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public decimal CommissionPerTrade { get; init; }
    public long SlippageTicks { get; init; }
}
