using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.WebApi.Contracts;

public sealed record BacktestRunResponse
{
    public required Guid Id { get; init; }
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required Dictionary<string, object> Parameters { get; init; }
    public required List<DataSubscriptionResponse> DataSubscriptions { get; init; }
    public required decimal InitialCash { get; init; }
    public required decimal Commission { get; init; }
    public required int SlippageTicks { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required DateTimeOffset DataStart { get; init; }
    public required DateTimeOffset DataEnd { get; init; }
    public required long DurationMs { get; init; }
    public required int TotalBars { get; init; }
    public required Dictionary<string, object> Metrics { get; init; }
    public required bool HasCandleData { get; init; }
    public required string RunMode { get; init; }
    public Guid? OptimizationRunId { get; init; }
}

public sealed record DataSubscriptionResponse(string AssetName, string Exchange, string TimeFrame);

public sealed record EquityPointResponse(long TimestampMs, decimal Value);

public static class MetricsMapping
{
    public static Dictionary<string, object> ToDict(PerformanceMetrics m) => new()
    {
        ["totalTrades"] = m.TotalTrades,
        ["winningTrades"] = m.WinningTrades,
        ["losingTrades"] = m.LosingTrades,
        ["netProfit"] = m.NetProfit,
        ["grossProfit"] = m.GrossProfit,
        ["grossLoss"] = m.GrossLoss,
        ["totalCommissions"] = m.TotalCommissions,
        ["totalReturnPct"] = m.TotalReturnPct,
        ["annualizedReturnPct"] = m.AnnualizedReturnPct,
        ["sharpeRatio"] = m.SharpeRatio,
        ["sortinoRatio"] = m.SortinoRatio,
        ["maxDrawdownPct"] = m.MaxDrawdownPct,
        ["winRatePct"] = m.WinRatePct,
        ["profitFactor"] = m.ProfitFactor,
        ["averageWin"] = m.AverageWin,
        ["averageLoss"] = m.AverageLoss,
        ["initialCapital"] = m.InitialCapital,
        ["finalEquity"] = m.FinalEquity,
        ["tradingDays"] = m.TradingDays,
    };
}

public sealed record OptimizationRunResponse
{
    public required Guid Id { get; init; }
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required long DurationMs { get; init; }
    public required long TotalCombinations { get; init; }
    public required string SortBy { get; init; }
    public required DateTimeOffset DataStart { get; init; }
    public required DateTimeOffset DataEnd { get; init; }
    public required decimal InitialCash { get; init; }
    public required decimal Commission { get; init; }
    public required int SlippageTicks { get; init; }
    public required int MaxParallelism { get; init; }
    public required List<DataSubscriptionResponse> DataSubscriptions { get; init; }
    public required List<BacktestRunResponse> Trials { get; init; }
}
