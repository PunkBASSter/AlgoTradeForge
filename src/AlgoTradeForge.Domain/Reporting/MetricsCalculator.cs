using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Reporting;

public class MetricsCalculator : IMetricsCalculator
{
    private const double RiskFreeRate = 0.02;
    private const int TradingDaysPerYear = 252;

    public PerformanceMetrics Calculate(
        IReadOnlyList<Fill> fills,
        IReadOnlyList<decimal> equityCurve,
        decimal initialCash,
        DateTimeOffset startTime,
        DateTimeOffset endTime)
    {
        var finalEquity = equityCurve.Count > 0 ? equityCurve[^1] : initialCash;
        var totalDays = (endTime - startTime).TotalDays;
        var tradingDays = (int)Math.Ceiling(totalDays);

        if (fills.Count == 0 || equityCurve.Count == 0)
            return CreateEmptyMetrics(initialCash, finalEquity, tradingDays);

        var tradeStats = ComputeTradeStatistics(fills);
        var curve = BuildDoubleCurve(equityCurve);
        var maxDrawdown = ComputeMaxDrawdown(curve);
        var years = totalDays / 365.25;
        var periodsPerYear = years > 0 ? equityCurve.Count / years : TradingDaysPerYear;
        var (sharpe, sortino) = ComputeRiskMetrics(curve, periodsPerYear);

        var totalReturn = initialCash != 0
            ? (double)((finalEquity - initialCash) / initialCash * 100)
            : 0;

        var annualizedReturn = years > 0 && initialCash != 0
            ? (Math.Pow((double)(finalEquity / initialCash), 1.0 / years) - 1) * 100
            : 0;

        var winRate = tradeStats.RoundTrips > 0
            ? (double)tradeStats.WinningTrades / tradeStats.RoundTrips * 100
            : 0;

        var profitFactor = tradeStats.GrossLoss > 0
            ? tradeStats.GrossProfit / tradeStats.GrossLoss
            : tradeStats.GrossProfit > 0 ? double.PositiveInfinity : 0;

        return new PerformanceMetrics
        {
            TotalTrades = fills.Count,
            WinningTrades = tradeStats.WinningTrades,
            LosingTrades = tradeStats.LosingTrades,
            NetProfit = (decimal)(tradeStats.GrossProfit - tradeStats.GrossLoss),
            GrossProfit = (decimal)tradeStats.GrossProfit,
            GrossLoss = (decimal)tradeStats.GrossLoss,
            TotalReturnPct = totalReturn,
            AnnualizedReturnPct = annualizedReturn,
            SharpeRatio = sharpe,
            SortinoRatio = sortino,
            MaxDrawdownPct = maxDrawdown,
            WinRatePct = winRate,
            ProfitFactor = profitFactor,
            AverageWin = tradeStats.WinningTrades > 0 ? tradeStats.GrossProfit / tradeStats.WinningTrades : 0,
            AverageLoss = tradeStats.LosingTrades > 0 ? tradeStats.GrossLoss / tradeStats.LosingTrades : 0,
            InitialCapital = initialCash,
            FinalEquity = finalEquity,
            TradingDays = tradingDays
        };
    }

    private static TradeStatistics ComputeTradeStatistics(IReadOnlyList<Fill> fills)
    {
        var stats = new TradeStatistics();
        var positions = new Dictionary<string, (decimal Quantity, decimal AvgEntry, Asset Asset)>();

        foreach (var fill in fills)
        {
            var key = fill.Asset.Name;
            var multiplier = fill.Asset.Multiplier;

            if (!positions.TryGetValue(key, out var pos))
                pos = (0m, 0m, fill.Asset);

            var direction = fill.Side == OrderSide.Buy ? 1 : -1;
            var fillQuantity = fill.Quantity * direction;
            var newQuantity = pos.Quantity + fillQuantity;

            if (pos.Quantity != 0 && Math.Sign(newQuantity) != Math.Sign(pos.Quantity))
            {
                // Full reversal — close existing position
                var pnl = (double)(pos.Quantity * (fill.Price - pos.AvgEntry) * multiplier);
                RecordPnl(stats, pnl);
                pos = (newQuantity, fill.Price, fill.Asset);
            }
            else if (pos.Quantity == 0)
            {
                pos = (newQuantity, fill.Price, fill.Asset);
            }
            else if (Math.Abs(newQuantity) < Math.Abs(pos.Quantity))
            {
                // Partial close
                var closedQuantity = Math.Abs(pos.Quantity) - Math.Abs(newQuantity);
                var pnl = (double)(closedQuantity * (fill.Price - pos.AvgEntry) * Math.Sign(pos.Quantity) * multiplier);
                RecordPnl(stats, pnl);
                pos = (newQuantity, pos.AvgEntry, fill.Asset);
            }
            else
            {
                // Adding to position — weighted average entry
                var totalCost = pos.Quantity * pos.AvgEntry + fillQuantity * fill.Price;
                pos = (newQuantity, totalCost / newQuantity, fill.Asset);
            }

            positions[key] = pos;
        }

        return stats;
    }

    private static void RecordPnl(TradeStatistics stats, double pnl)
    {
        if (pnl > 0)
        {
            stats.WinningTrades++;
            stats.GrossProfit += pnl;
        }
        else if (pnl < 0)
        {
            stats.LosingTrades++;
            stats.GrossLoss += Math.Abs(pnl);
        }
        stats.RoundTrips++;
    }

    private static List<double> BuildDoubleCurve(IReadOnlyList<decimal> equityCurve)
    {
        var curve = new List<double>(equityCurve.Count);
        foreach (var e in equityCurve)
            curve.Add((double)e);
        return curve;
    }

    private static double ComputeMaxDrawdown(List<double> equityCurve)
    {
        if (equityCurve.Count == 0)
            return 0;

        double peak = equityCurve[0];
        double maxDrawdown = 0;

        foreach (var equity in equityCurve)
        {
            if (equity > peak)
                peak = equity;

            if (peak > 0)
            {
                var drawdown = (peak - equity) / peak * 100;
                if (drawdown > maxDrawdown)
                    maxDrawdown = drawdown;
            }
        }

        return maxDrawdown;
    }

    private static (double sharpe, double sortino) ComputeRiskMetrics(List<double> equityCurve, double periodsPerYear)
    {
        if (equityCurve.Count < 2)
            return (0, 0);

        var returns = new List<double>(equityCurve.Count - 1);
        for (var i = 1; i < equityCurve.Count; i++)
        {
            if (equityCurve[i - 1] > 0)
                returns.Add((equityCurve[i] - equityCurve[i - 1]) / equityCurve[i - 1]);
        }

        if (returns.Count == 0)
            return (0, 0);

        double sum = 0, sumSq = 0, negSumSq = 0;
        var negCount = 0;

        foreach (var r in returns)
        {
            sum += r;
            sumSq += r * r;
            if (r < 0) { negSumSq += r * r; negCount++; }
        }

        var n = returns.Count;
        var mean = sum / n;
        var variance = sumSq / n - mean * mean;
        var stdDev = Math.Sqrt(Math.Max(0, variance));
        var riskFreeRatePerPeriod = RiskFreeRate / periodsPerYear;
        var annualizationFactor = Math.Sqrt(periodsPerYear);

        var sharpe = stdDev > 0
            ? (mean - riskFreeRatePerPeriod) / stdDev * annualizationFactor
            : 0;

        var sortino = 0.0;
        if (negCount > 0)
        {
            var downwardStdDev = Math.Sqrt(negSumSq / negCount);
            sortino = downwardStdDev > 0
                ? (mean - riskFreeRatePerPeriod) / downwardStdDev * annualizationFactor
                : 0;
        }
        else if (mean > riskFreeRatePerPeriod)
        {
            sortino = double.PositiveInfinity;
        }

        return (sharpe, sortino);
    }

    private static PerformanceMetrics CreateEmptyMetrics(decimal initialCapital, decimal finalEquity, int tradingDays)
    {
        return new PerformanceMetrics
        {
            TotalTrades = 0, WinningTrades = 0, LosingTrades = 0,
            NetProfit = 0, GrossProfit = 0, GrossLoss = 0,
            TotalReturnPct = 0, AnnualizedReturnPct = 0,
            SharpeRatio = 0, SortinoRatio = 0, MaxDrawdownPct = 0,
            WinRatePct = 0, ProfitFactor = 0, AverageWin = 0, AverageLoss = 0,
            InitialCapital = initialCapital, FinalEquity = finalEquity, TradingDays = tradingDays
        };
    }

    private class TradeStatistics
    {
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public int RoundTrips { get; set; }
        public double GrossProfit { get; set; }
        public double GrossLoss { get; set; }
    }
}
