using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Backtests;

internal static class MetricsScaler
{
    public static PerformanceMetrics ScaleDown(PerformanceMetrics metrics, ScaleContext scale)
    {
        return metrics with
        {
            InitialCapital = scale.TicksToAmount(metrics.InitialCapital),
            FinalEquity = scale.TicksToAmount(metrics.FinalEquity),
            NetProfit = scale.TicksToAmount(metrics.NetProfit),
            GrossProfit = scale.TicksToAmount(metrics.GrossProfit),
            GrossLoss = scale.TicksToAmount(metrics.GrossLoss),
            TotalCommissions = scale.TicksToAmount(metrics.TotalCommissions),
            AverageWin = (double)scale.TicksToAmount((decimal)metrics.AverageWin),
            AverageLoss = (double)scale.TicksToAmount((decimal)metrics.AverageLoss),
        };
    }

    public static EquityPoint[] ScaleEquityCurve(IReadOnlyList<EquitySnapshot> curve, ScaleContext scale)
    {
        var result = new EquityPoint[curve.Count];
        for (var i = 0; i < curve.Count; i++)
            result[i] = new EquityPoint(curve[i].TimestampMs, scale.TicksToAmount(curve[i].Value));
        return result;
    }

    public static TradePoint[] ScaleTradePnl(IReadOnlyList<ClosedTrade> trades, ScaleContext scale)
    {
        var result = new TradePoint[trades.Count];
        for (var i = 0; i < trades.Count; i++)
            result[i] = new TradePoint(
                trades[i].ExitTimestampMs,
                scale.TicksToAmount(trades[i].RealizedPnl));
        return result;
    }
}
