using AlgoTradeForge.Application;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.WebApi.Contracts;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Limit, int Offset, bool HasMore);

public sealed record BacktestRunResponse
{
    public required Guid Id { get; init; }
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required Dictionary<string, object> Parameters { get; init; }
    public required DataSubscriptionDto DataSubscription { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required long DurationMs { get; init; }
    public required int TotalBars { get; init; }
    public required Dictionary<string, object> Metrics { get; init; }
    public required bool HasCandleData { get; init; }
    public required string RunMode { get; init; }
    public Guid? OptimizationRunId { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorStackTrace { get; init; }
}

public sealed record EquityPointResponse(long TimestampMs, double Value);

public sealed record TradePointResponse(long TimestampMs, double Pnl);

public static class MetricsMapping
{
    public static Dictionary<string, object> ToDict(PerformanceMetrics m, double? fitnessScore = null)
    {
        var dict = new Dictionary<string, object>
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
        if (fitnessScore.HasValue)
            dict["fitness"] = fitnessScore.Value;
        return dict;
    }
}

public sealed record FailedTrialResponse
{
    public required string ExceptionType { get; init; }
    public required string ExceptionMessage { get; init; }
    public string? StackTrace { get; init; }
    public required Dictionary<string, object> SampleParameters { get; init; }
    public required long OccurrenceCount { get; init; }
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
    public long FilteredTrials { get; init; }
    public long FailedTrials { get; init; }
    public required string SortBy { get; init; }
    public required DataSubscriptionDto DataSubscription { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public required int MaxParallelism { get; init; }
    public required List<BacktestRunResponse> Trials { get; init; }
    public List<FailedTrialResponse> FailedTrialDetails { get; init; } = [];
    public string? OptimizationMethod { get; init; }
    public int? GenerationsCompleted { get; init; }
    public string? InputJson { get; init; }
    public required string Status { get; init; }
    public string? ErrorMessage { get; init; }
}
