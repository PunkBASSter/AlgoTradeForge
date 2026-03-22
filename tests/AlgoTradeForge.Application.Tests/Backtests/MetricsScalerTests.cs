using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Reporting;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Backtests;

public class MetricsScalerTests
{
    // tickSize=0.01 → scaleFactor=100, so 1000 ticks = 10.00 in real terms
    private readonly ScaleContext _scale = new(0.01m);

    private static PerformanceMetrics MakeMetrics(
        decimal initialCapital = 100_000,
        decimal finalEquity = 110_000,
        decimal netProfit = 10_000,
        decimal grossProfit = 15_000,
        decimal grossLoss = -5_000,
        decimal totalCommissions = 200,
        double averageWin = 500.0,
        double averageLoss = -250.0,
        int totalTrades = 20,
        int winningTrades = 12,
        int losingTrades = 8,
        double winRatePct = 60.0,
        double sharpeRatio = 1.5,
        double sortinoRatio = 2.0,
        double maxDrawdownPct = 5.0,
        double profitFactor = 3.0,
        double totalReturnPct = 10.0,
        double annualizedReturnPct = 12.0,
        int tradingDays = 252) =>
        new()
        {
            InitialCapital = initialCapital,
            FinalEquity = finalEquity,
            NetProfit = netProfit,
            GrossProfit = grossProfit,
            GrossLoss = grossLoss,
            TotalCommissions = totalCommissions,
            AverageWin = averageWin,
            AverageLoss = averageLoss,
            TotalTrades = totalTrades,
            WinningTrades = winningTrades,
            LosingTrades = losingTrades,
            WinRatePct = winRatePct,
            SharpeRatio = sharpeRatio,
            SortinoRatio = sortinoRatio,
            MaxDrawdownPct = maxDrawdownPct,
            ProfitFactor = profitFactor,
            TotalReturnPct = totalReturnPct,
            AnnualizedReturnPct = annualizedReturnPct,
            TradingDays = tradingDays,
        };

    [Fact]
    public void ScaleDown_ConvertsAllMonetaryFields()
    {
        var metrics = MakeMetrics();

        var scaled = MetricsScaler.ScaleDown(metrics, _scale);

        // 100_000 ticks * 0.01 tickSize = 1000.00
        Assert.Equal(1000.00m, scaled.InitialCapital);
        Assert.Equal(1100.00m, scaled.FinalEquity);
        Assert.Equal(100.00m, scaled.NetProfit);
        Assert.Equal(150.00m, scaled.GrossProfit);
        Assert.Equal(-50.00m, scaled.GrossLoss);
        Assert.Equal(2.00m, scaled.TotalCommissions);
    }

    [Fact]
    public void ScaleDown_PreservesNonMonetaryFields()
    {
        var metrics = MakeMetrics();

        var scaled = MetricsScaler.ScaleDown(metrics, _scale);

        Assert.Equal(20, scaled.TotalTrades);
        Assert.Equal(12, scaled.WinningTrades);
        Assert.Equal(8, scaled.LosingTrades);
        Assert.Equal(60.0, scaled.WinRatePct);
        Assert.Equal(1.5, scaled.SharpeRatio);
        Assert.Equal(2.0, scaled.SortinoRatio);
        Assert.Equal(5.0, scaled.MaxDrawdownPct);
        Assert.Equal(3.0, scaled.ProfitFactor);
        Assert.Equal(10.0, scaled.TotalReturnPct);
        Assert.Equal(12.0, scaled.AnnualizedReturnPct);
        Assert.Equal(252, scaled.TradingDays);
    }

    [Fact]
    public void ScaleDown_AverageWinLoss_HandlesDoubleDecimalRoundTrip()
    {
        // AverageWin/Loss go through double→decimal→TicksToAmount→double
        var metrics = MakeMetrics(averageWin: 1234.0, averageLoss: -567.0);

        var scaled = MetricsScaler.ScaleDown(metrics, _scale);

        // 1234 ticks * 0.01 = 12.34 → (double)12.34
        Assert.Equal(12.34, scaled.AverageWin, precision: 10);
        Assert.Equal(-5.67, scaled.AverageLoss, precision: 10);
    }

    [Fact]
    public void ScaleEquityCurve_ConvertsAllPoints()
    {
        var curve = new List<EquitySnapshot>
        {
            new(1000L, 100_000),
            new(2000L, 105_000),
            new(3000L, 110_000),
        };

        var result = MetricsScaler.ScaleEquityCurve(curve, _scale);

        Assert.Equal(3, result.Length);
        Assert.Equal(1000L, result[0].TimestampMs);
        Assert.Equal(1000.00m, result[0].Value);
        Assert.Equal(2000L, result[1].TimestampMs);
        Assert.Equal(1050.00m, result[1].Value);
        Assert.Equal(3000L, result[2].TimestampMs);
        Assert.Equal(1100.00m, result[2].Value);
    }

    [Fact]
    public void ScaleEquityCurve_EmptyCurve_ReturnsEmpty()
    {
        var curve = new List<EquitySnapshot>();

        var result = MetricsScaler.ScaleEquityCurve(curve, _scale);

        Assert.Empty(result);
    }

    [Fact]
    public void ScaleTradePnl_ConvertsAllPoints()
    {
        var trades = new List<ClosedTrade>
        {
            new(1000L, 5000L),   // win: 5000 ticks * 0.01 = 50.00
            new(2000L, -3000L),  // loss: -3000 ticks * 0.01 = -30.00
        };

        var result = MetricsScaler.ScaleTradePnl(trades, _scale);

        Assert.Equal(2, result.Length);
        Assert.Equal(1000L, result[0].TimestampMs);
        Assert.Equal(50.00m, result[0].Pnl);
        Assert.Equal(2000L, result[1].TimestampMs);
        Assert.Equal(-30.00m, result[1].Pnl);
    }

    [Fact]
    public void ScaleTradePnl_EmptyList_ReturnsEmpty()
    {
        var trades = new List<ClosedTrade>();

        var result = MetricsScaler.ScaleTradePnl(trades, _scale);

        Assert.Empty(result);
    }
}
