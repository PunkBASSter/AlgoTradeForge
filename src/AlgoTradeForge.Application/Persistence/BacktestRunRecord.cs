using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Persistence;

public sealed record BacktestRunRecord
{
    public required Guid Id { get; init; }
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required IReadOnlyDictionary<string, object> Parameters { get; init; }
    public required DataSubscriptionDto DataSubscription { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required long DurationMs { get; init; }
    public required int TotalBars { get; init; }
    public required PerformanceMetrics Metrics { get; init; }
    public required IReadOnlyList<EquityPoint> EquityCurve { get; init; }
    public IReadOnlyList<TradePoint> TradePnl { get; init; } = [];
    public string? RunFolderPath { get; init; }
    public required string RunMode { get; init; }
    public Guid? OptimizationRunId { get; init; }
    public double? FitnessScore { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorStackTrace { get; init; }
}

public sealed record EquityPoint(long TimestampMs, double Value);

public sealed record TradePoint(long TimestampMs, double Pnl);

public static class RunModes
{
    public const string Backtest = "Backtest";
    public const string Cancelled = "Cancelled";
    public const string Failed = "Failed";
}
