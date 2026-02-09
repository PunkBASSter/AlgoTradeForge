namespace AlgoTradeForge.Domain.Strategy;

public class StrategyParamsBase
{
    public required Asset MainAsset { get; init; }
    public required TimeSpan WorkingTimeFrame { get; init; } //Timeframe to run logic on
    public required TimeSpan ReportingTimeFrame { get; init; } //for charts in reports, visual and AI debugging based on exported events
}