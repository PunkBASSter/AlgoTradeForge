namespace AlgoTradeForge.Domain.Reporting;

public static class MetricNames
{
    public const string TotalTrades = nameof(PerformanceMetrics.TotalTrades);
    public const string WinningTrades = nameof(PerformanceMetrics.WinningTrades);
    public const string LosingTrades = nameof(PerformanceMetrics.LosingTrades);
    public const string NetProfit = nameof(PerformanceMetrics.NetProfit);
    public const string GrossProfit = nameof(PerformanceMetrics.GrossProfit);
    public const string GrossLoss = nameof(PerformanceMetrics.GrossLoss);
    public const string TotalCommissions = nameof(PerformanceMetrics.TotalCommissions);
    public const string TotalReturnPct = nameof(PerformanceMetrics.TotalReturnPct);
    public const string AnnualizedReturnPct = nameof(PerformanceMetrics.AnnualizedReturnPct);
    public const string SharpeRatio = nameof(PerformanceMetrics.SharpeRatio);
    public const string SortinoRatio = nameof(PerformanceMetrics.SortinoRatio);
    public const string MaxDrawdownPct = nameof(PerformanceMetrics.MaxDrawdownPct);
    public const string WinRatePct = nameof(PerformanceMetrics.WinRatePct);
    public const string ProfitFactor = nameof(PerformanceMetrics.ProfitFactor);
    public const string AverageWin = nameof(PerformanceMetrics.AverageWin);
    public const string AverageLoss = nameof(PerformanceMetrics.AverageLoss);
    public const string InitialCapital = nameof(PerformanceMetrics.InitialCapital);
    public const string FinalEquity = nameof(PerformanceMetrics.FinalEquity);
    public const string TradingDays = nameof(PerformanceMetrics.TradingDays);

    public const string Default = SharpeRatio;
}
