using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Reporting;

public class MetricsCalculator : IMetricsCalculator
{
    protected const double RiskFreeRate = 0.02;
    protected const int TradingDaysPerYear = 252;

    public virtual PerformanceMetrics Calculate(
        IReadOnlyList<Fill> fills,
        IReadOnlyList<Bar> bars,
        Portfolio portfolio,
        decimal finalPrice,
        Asset asset)
    {
        var initialCapital = portfolio.InitialCash;
        var finalEquity = portfolio.Equity(finalPrice);
        var tradingDays = bars.Count;

        if (fills.Count == 0 || bars.Count == 0)
        {
            return CreateEmptyMetrics(initialCapital, finalEquity, tradingDays);
        }

        var tradeStats = ComputeTradeStatistics(fills, asset);
        var equityCurve = BuildEquityCurve(fills, bars, initialCapital, asset);
        var maxDrawdown = ComputeMaxDrawdown(equityCurve);
        var (sharpe, sortino) = ComputeRiskMetrics(equityCurve);

        var totalReturn = initialCapital != 0
            ? (double)((finalEquity - initialCapital) / initialCapital * 100)
            : 0;

        var annualizedReturn = initialCapital != 0 && tradingDays > 0
            ? (Math.Pow((double)(finalEquity / initialCapital), (double)TradingDaysPerYear / tradingDays) - 1) * 100
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
            InitialCapital = initialCapital,
            FinalEquity = finalEquity,
            TradingDays = tradingDays
        };
    }

    protected virtual TradeStatistics ComputeTradeStatistics(IReadOnlyList<Fill> fills, Asset asset)
    {
        var stats = new TradeStatistics();
        decimal position = 0;
        decimal avgEntryPrice = 0;

        foreach (var fill in fills)
        {
            var direction = fill.Side == OrderSide.Buy ? 1 : -1;
            var fillQuantity = fill.Quantity * direction;
            var newPosition = position + fillQuantity;

            if (position != 0 && Math.Sign(newPosition) != Math.Sign(position))
            {
                var pnl = (double)(position * (fill.Price - avgEntryPrice) * asset.Multiplier);
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
                avgEntryPrice = fill.Price;
            }
            else if (position == 0)
            {
                avgEntryPrice = fill.Price;
            }
            else if (Math.Abs(newPosition) < Math.Abs(position))
            {
                var closedQuantity = Math.Abs(position) - Math.Abs(newPosition);
                var pnl = (double)(closedQuantity * (fill.Price - avgEntryPrice) * Math.Sign(position) * asset.Multiplier);
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
            else
            {
                var totalCost = position * avgEntryPrice + fillQuantity * fill.Price;
                avgEntryPrice = totalCost / newPosition;
            }

            position = newPosition;
        }

        return stats;
    }

    protected virtual List<double> BuildEquityCurve(
        IReadOnlyList<Fill> fills,
        IReadOnlyList<Bar> bars,
        decimal initialCapital,
        Asset asset)
    {
        var curve = new List<double>(bars.Count);
        var fillIndex = 0;
        decimal cash = initialCapital;
        decimal position = 0;
        decimal avgEntryPrice = 0;

        foreach (var bar in bars)
        {
            while (fillIndex < fills.Count && fills[fillIndex].Timestamp <= bar.Timestamp)
            {
                var fill = fills[fillIndex];
                var direction = fill.Side == OrderSide.Buy ? 1 : -1;
                cash += fill.Price * fill.Quantity * asset.Multiplier * -direction - fill.Commission;

                var fillQuantity = fill.Quantity * direction;
                var newPosition = position + fillQuantity;

                if (position == 0)
                {
                    avgEntryPrice = fill.Price;
                }
                else if (Math.Sign(newPosition) == Math.Sign(position) && Math.Abs(newPosition) > Math.Abs(position))
                {
                    var totalCost = position * avgEntryPrice + fillQuantity * fill.Price;
                    avgEntryPrice = totalCost / newPosition;
                }
                else if (Math.Sign(newPosition) != Math.Sign(position))
                {
                    avgEntryPrice = fill.Price;
                }

                position = newPosition;
                fillIndex++;
            }

            var equity = cash + position * bar.Close * asset.Multiplier;
            curve.Add((double)equity);
        }

        return curve;
    }

    protected virtual double ComputeMaxDrawdown(List<double> equityCurve)
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

    protected virtual (double sharpe, double sortino) ComputeRiskMetrics(List<double> equityCurve)
    {
        if (equityCurve.Count < 2)
            return (0, 0);

        var returns = new List<double>(equityCurve.Count - 1);
        for (var i = 1; i < equityCurve.Count; i++)
        {
            if (equityCurve[i - 1] > 0)
            {
                var ret = (equityCurve[i] - equityCurve[i - 1]) / equityCurve[i - 1];
                returns.Add(ret);
            }
        }

        if (returns.Count == 0)
            return (0, 0);

        double sum = 0;
        double sumSq = 0;
        double negSumSq = 0;
        var negCount = 0;

        foreach (var r in returns)
        {
            sum += r;
            sumSq += r * r;
            if (r < 0)
            {
                negSumSq += r * r;
                negCount++;
            }
        }

        var n = returns.Count;
        var mean = sum / n;
        var variance = sumSq / n - mean * mean;
        var stdDev = Math.Sqrt(Math.Max(0, variance));

        var dailyRiskFreeRate = RiskFreeRate / TradingDaysPerYear;

        var sharpe = stdDev > 0
            ? (mean - dailyRiskFreeRate) / stdDev * Math.Sqrt(TradingDaysPerYear)
            : 0;

        var sortino = 0.0;
        if (negCount > 0)
        {
            var downwardStdDev = Math.Sqrt(negSumSq / negCount);
            sortino = downwardStdDev > 0
                ? (mean - dailyRiskFreeRate) / downwardStdDev * Math.Sqrt(TradingDaysPerYear)
                : 0;
        }
        else if (mean > dailyRiskFreeRate)
        {
            sortino = double.PositiveInfinity;
        }

        return (sharpe, sortino);
    }

    protected virtual PerformanceMetrics CreateEmptyMetrics(decimal initialCapital, decimal finalEquity, int tradingDays)
    {
        return new PerformanceMetrics
        {
            TotalTrades = 0,
            WinningTrades = 0,
            LosingTrades = 0,
            NetProfit = 0,
            GrossProfit = 0,
            GrossLoss = 0,
            TotalReturnPct = 0,
            AnnualizedReturnPct = 0,
            SharpeRatio = 0,
            SortinoRatio = 0,
            MaxDrawdownPct = 0,
            WinRatePct = 0,
            ProfitFactor = 0,
            AverageWin = 0,
            AverageLoss = 0,
            InitialCapital = initialCapital,
            FinalEquity = finalEquity,
            TradingDays = tradingDays
        };
    }

    protected class TradeStatistics
    {
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public int RoundTrips { get; set; }
        public double GrossProfit { get; set; }
        public double GrossLoss { get; set; }
    }
}
