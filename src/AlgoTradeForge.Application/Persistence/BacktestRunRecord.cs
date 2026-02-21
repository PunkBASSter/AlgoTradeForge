using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Persistence;

public sealed record BacktestRunRecord
{
    public required Guid Id { get; init; }
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required IReadOnlyDictionary<string, object> Parameters { get; init; }
    public required string AssetName { get; init; }
    public required string Exchange { get; init; }
    public required string TimeFrame { get; init; }
    public required decimal InitialCash { get; init; }
    public required decimal Commission { get; init; }
    public required int SlippageTicks { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required DateTimeOffset DataStart { get; init; }
    public required DateTimeOffset DataEnd { get; init; }
    public required long DurationMs { get; init; }
    public required int TotalBars { get; init; }
    public required PerformanceMetrics Metrics { get; init; }
    public required IReadOnlyList<EquityPoint> EquityCurve { get; init; }
    public string? RunFolderPath { get; init; }
    public required string RunMode { get; init; }
    public Guid? OptimizationRunId { get; init; }
}

public sealed record EquityPoint(long TimestampMs, decimal Value);
