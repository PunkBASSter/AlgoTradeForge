using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Domain.Reporting;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public sealed class TrialFilterTests
{
    private static readonly PerformanceMetrics GoodMetrics = new()
    {
        TotalTrades = 50, WinningTrades = 30, LosingTrades = 20,
        NetProfit = 5_000m, GrossProfit = 8_000m, GrossLoss = -3_000m, TotalCommissions = 100m,
        TotalReturnPct = 50.0, AnnualizedReturnPct = 25.0,
        SharpeRatio = 2.0, SortinoRatio = 2.5, MaxDrawdownPct = 15.0,
        WinRatePct = 60.0, ProfitFactor = 2.67, AverageWin = 266.7, AverageLoss = -150.0,
        InitialCapital = 10_000m, FinalEquity = 15_000m, TradingDays = 365,
    };

    private static RunOptimizationCommand MakeCommand(
        double? minProfitFactor = null,
        double? maxDrawdownPct = null,
        double? minSharpeRatio = null,
        double? minSortinoRatio = null,
        double? minAnnualizedReturnPct = null) => new()
    {
        StrategyName = "Test",
        InitialCash = 10_000m,
        StartTime = DateTimeOffset.UtcNow.AddDays(-30),
        EndTime = DateTimeOffset.UtcNow,
        MinProfitFactor = minProfitFactor,
        MaxDrawdownPct = maxDrawdownPct,
        MinSharpeRatio = minSharpeRatio,
        MinSortinoRatio = minSortinoRatio,
        MinAnnualizedReturnPct = minAnnualizedReturnPct,
    };

    [Fact]
    public void All_null_filters_passes_everything()
    {
        var filter = new TrialFilter(MakeCommand());
        Assert.True(filter.Passes(GoodMetrics));
    }

    [Fact]
    public void All_null_filters_passes_zero_metrics()
    {
        var filter = new TrialFilter(MakeCommand());
        var zeroMetrics = new PerformanceMetrics
        {
            TotalTrades = 0, WinningTrades = 0, LosingTrades = 0,
            NetProfit = 0m, GrossProfit = 0m, GrossLoss = 0m, TotalCommissions = 0m,
            TotalReturnPct = 0, AnnualizedReturnPct = 0,
            SharpeRatio = 0, SortinoRatio = 0, MaxDrawdownPct = 0,
            WinRatePct = 0, ProfitFactor = 0, AverageWin = 0, AverageLoss = 0,
            InitialCapital = 10_000m, FinalEquity = 10_000m, TradingDays = 0,
        };
        Assert.True(filter.Passes(zeroMetrics));
    }

    [Theory]
    [InlineData(2.0, true)]   // above threshold
    [InlineData(1.2, true)]   // exactly at threshold
    [InlineData(0.8, false)]  // below threshold
    public void MinProfitFactor_filters_correctly(double profitFactor, bool expected)
    {
        var filter = new TrialFilter(MakeCommand(minProfitFactor: 1.2));
        var metrics = GoodMetrics with { ProfitFactor = profitFactor };
        Assert.Equal(expected, filter.Passes(metrics));
    }

    [Theory]
    [InlineData(10.0, true)]  // below threshold
    [InlineData(40.0, true)]  // exactly at threshold
    [InlineData(50.0, false)] // above threshold
    public void MaxDrawdownPct_filters_correctly(double drawdown, bool expected)
    {
        var filter = new TrialFilter(MakeCommand(maxDrawdownPct: 40.0));
        var metrics = GoodMetrics with { MaxDrawdownPct = drawdown };
        Assert.Equal(expected, filter.Passes(metrics));
    }

    [Theory]
    [InlineData(1.5, true)]   // above threshold
    [InlineData(0.5, true)]   // exactly at threshold
    [InlineData(0.3, false)]  // below threshold
    public void MinSharpeRatio_filters_correctly(double sharpe, bool expected)
    {
        var filter = new TrialFilter(MakeCommand(minSharpeRatio: 0.5));
        var metrics = GoodMetrics with { SharpeRatio = sharpe };
        Assert.Equal(expected, filter.Passes(metrics));
    }

    [Theory]
    [InlineData(2.0, true)]
    [InlineData(0.5, true)]   // exactly at threshold
    [InlineData(0.2, false)]
    public void MinSortinoRatio_filters_correctly(double sortino, bool expected)
    {
        var filter = new TrialFilter(MakeCommand(minSortinoRatio: 0.5));
        var metrics = GoodMetrics with { SortinoRatio = sortino };
        Assert.Equal(expected, filter.Passes(metrics));
    }

    [Theory]
    [InlineData(10.0, true)]
    [InlineData(2.0, true)]   // exactly at threshold
    [InlineData(1.0, false)]
    public void MinAnnualizedReturnPct_filters_correctly(double annReturn, bool expected)
    {
        var filter = new TrialFilter(MakeCommand(minAnnualizedReturnPct: 2.0));
        var metrics = GoodMetrics with { AnnualizedReturnPct = annReturn };
        Assert.Equal(expected, filter.Passes(metrics));
    }

    [Fact]
    public void Multiple_filters_all_must_pass()
    {
        var filter = new TrialFilter(MakeCommand(
            minProfitFactor: 1.5,
            maxDrawdownPct: 20.0,
            minSharpeRatio: 1.0));

        // All pass
        Assert.True(filter.Passes(GoodMetrics));

        // One fails (drawdown too high)
        var badDrawdown = GoodMetrics with { MaxDrawdownPct = 30.0 };
        Assert.False(filter.Passes(badDrawdown));

        // One fails (profit factor too low)
        var badPf = GoodMetrics with { ProfitFactor = 1.0 };
        Assert.False(filter.Passes(badPf));
    }
}
