using AlgoTradeForge.Application;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.WebApi.Contracts;

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Limit, int Offset, bool HasMore);

public sealed record BacktestRunResponse
{
    public required Guid Id { get; init; }
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required Dictionary<string, object> Parameters { get; init; }
    public required IReadOnlyList<DataFeedSubscription> DataSubscriptions { get; init; }
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
    public string? Params { get; init; }
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
            ["initialCapital"] = m.InitialCapital,
            ["finalEquity"] = m.FinalEquity,
            ["tradingDays"] = m.TradingDays,
        };

        AddIfFinite(dict, "totalReturnPct", m.TotalReturnPct);
        AddIfFinite(dict, "annualizedReturnPct", m.AnnualizedReturnPct);
        AddIfFinite(dict, "sharpeRatio", m.SharpeRatio);
        AddIfFinite(dict, "sortinoRatio", m.SortinoRatio);
        AddIfFinite(dict, "maxDrawdownPct", m.MaxDrawdownPct);
        AddIfFinite(dict, "winRatePct", m.WinRatePct);
        AddIfFinite(dict, "profitFactor", m.ProfitFactor);
        AddIfFinite(dict, "averageWin", m.AverageWin);
        AddIfFinite(dict, "averageLoss", m.AverageLoss);
        dict["netTicks"] = m.NetTicks;
        AddIfFinite(dict, "avgTicksPerTrade", m.AvgTicksPerTrade);
        AddIfFinite(dict, "tickProfitFactor", m.TickProfitFactor);

        if (fitnessScore is { } fs && double.IsFinite(fs))
            dict["fitness"] = fs;

        return dict;
    }

    private static void AddIfFinite(Dictionary<string, object> dict, string key, double value)
    {
        if (double.IsFinite(value))
            dict[key] = value;
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
    public long DedupSkipped { get; init; }
    public required string SortBy { get; init; }
    public required IReadOnlyList<DataFeedSubscription> DataSubscriptions { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public required int MaxParallelism { get; init; }
    public int TrialCount { get; init; }
    public required List<BacktestRunResponse> Trials { get; init; }
    public List<FailedTrialResponse> FailedTrialDetails { get; init; } = [];
    public string? OptimizationMethod { get; init; }
    public int? GenerationsCompleted { get; init; }
    public string? InputJson { get; init; }
    public required string Status { get; init; }
    public string? ErrorMessage { get; init; }
    public Guid? GroupId { get; init; }
}
