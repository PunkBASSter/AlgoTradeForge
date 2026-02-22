using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Backtests;

internal static class MetricsScaler
{
    public static PerformanceMetrics ScaleDown(PerformanceMetrics metrics, decimal scaleFactor)
    {
        return metrics with
        {
            InitialCapital = metrics.InitialCapital / scaleFactor,
            FinalEquity = metrics.FinalEquity / scaleFactor,
            NetProfit = metrics.NetProfit / scaleFactor,
            GrossProfit = metrics.GrossProfit / scaleFactor,
            GrossLoss = metrics.GrossLoss / scaleFactor,
            TotalCommissions = metrics.TotalCommissions / scaleFactor,
            AverageWin = metrics.AverageWin / (double)scaleFactor,
            AverageLoss = metrics.AverageLoss / (double)scaleFactor,
        };
    }

    public static EquityPoint[] ScaleEquityCurve(IReadOnlyList<EquitySnapshot> curve, decimal scaleFactor)
    {
        var result = new EquityPoint[curve.Count];
        for (var i = 0; i < curve.Count; i++)
            result[i] = new EquityPoint(curve[i].TimestampMs, curve[i].Value / scaleFactor);
        return result;
    }
}
