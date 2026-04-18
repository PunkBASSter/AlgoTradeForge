using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Tests.Contracts;

public class MetricsMappingTests
{
    private static PerformanceMetrics CreateMetrics(
        double sharpe = 1.5,
        double sortino = 2.0,
        double profitFactor = 1.8,
        double maxDrawdownPct = 10.0,
        double winRatePct = 55.0,
        double totalReturnPct = 25.0,
        double annualizedReturnPct = 30.0,
        double averageWin = 100.0,
        double averageLoss = 50.0) => new()
    {
        TotalTrades = 100,
        WinningTrades = 55,
        LosingTrades = 45,
        NetProfit = 5000m,
        GrossProfit = 8000m,
        GrossLoss = 3000m,
        TotalCommissions = 200m,
        TotalReturnPct = totalReturnPct,
        AnnualizedReturnPct = annualizedReturnPct,
        SharpeRatio = sharpe,
        SortinoRatio = sortino,
        MaxDrawdownPct = maxDrawdownPct,
        WinRatePct = winRatePct,
        ProfitFactor = profitFactor,
        AverageWin = averageWin,
        AverageLoss = averageLoss,
        InitialCapital = 10_000m,
        FinalEquity = 15_000m,
        TradingDays = 252,
    };

    [Fact]
    public void ToDict_NormalMetrics_ContainsAllKeys()
    {
        var dict = MetricsMapping.ToDict(CreateMetrics(), fitnessScore: 0.85);

        Assert.Equal(1.5, dict["sharpeRatio"]);
        Assert.Equal(2.0, dict["sortinoRatio"]);
        Assert.Equal(1.8, dict["profitFactor"]);
        Assert.Equal(0.85, dict["fitness"]);
        Assert.Equal(100, dict["totalTrades"]);
        Assert.Equal(10_000m, dict["initialCapital"]);
    }

    [Fact]
    public void ToDict_NaNSharpe_OmitsSharpeKey()
    {
        var dict = MetricsMapping.ToDict(CreateMetrics(sharpe: double.NaN));

        Assert.False(dict.ContainsKey("sharpeRatio"));
        // Other finite metrics remain
        Assert.True(dict.ContainsKey("sortinoRatio"));
        Assert.True(dict.ContainsKey("profitFactor"));
    }

    [Fact]
    public void ToDict_InfinitySortino_OmitsSortinoKey()
    {
        var dict = MetricsMapping.ToDict(CreateMetrics(sortino: double.PositiveInfinity));

        Assert.False(dict.ContainsKey("sortinoRatio"));
        Assert.True(dict.ContainsKey("sharpeRatio"));
    }

    [Fact]
    public void ToDict_NegativeInfinityReturn_OmitsReturnKey()
    {
        var dict = MetricsMapping.ToDict(CreateMetrics(totalReturnPct: double.NegativeInfinity));

        Assert.False(dict.ContainsKey("totalReturnPct"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ToDict_NonFiniteFitness_OmitsFitnessKey(double fitness)
    {
        var dict = MetricsMapping.ToDict(CreateMetrics(), fitnessScore: fitness);

        Assert.False(dict.ContainsKey("fitness"));
    }

    [Fact]
    public void ToDict_NullFitness_OmitsFitnessKey()
    {
        var dict = MetricsMapping.ToDict(CreateMetrics(), fitnessScore: null);

        Assert.False(dict.ContainsKey("fitness"));
    }

    [Fact]
    public void ToDict_FiniteFitness_IncludesFitnessKey()
    {
        var dict = MetricsMapping.ToDict(CreateMetrics(), fitnessScore: 0.75);

        Assert.Equal(0.75, dict["fitness"]);
    }

    [Fact]
    public void ToDict_IntAndDecimalFields_AlwaysPresent()
    {
        // Integer and decimal fields cannot be NaN/Infinity — always included
        var dict = MetricsMapping.ToDict(CreateMetrics(
            sharpe: double.NaN,
            sortino: double.NaN,
            profitFactor: double.NaN,
            maxDrawdownPct: double.NaN,
            winRatePct: double.NaN,
            totalReturnPct: double.NaN,
            annualizedReturnPct: double.NaN,
            averageWin: double.NaN,
            averageLoss: double.NaN));

        Assert.Equal(100, dict["totalTrades"]);
        Assert.Equal(55, dict["winningTrades"]);
        Assert.Equal(45, dict["losingTrades"]);
        Assert.Equal(5000m, dict["netProfit"]);
        Assert.Equal(10_000m, dict["initialCapital"]);
        Assert.Equal(15_000m, dict["finalEquity"]);
        Assert.Equal(252, dict["tradingDays"]);
    }
}
