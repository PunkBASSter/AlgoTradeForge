using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Reporting;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Optimization.Genetic;

public class CompositeFitnessFunctionTests
{
    private readonly CompositeFitnessFunction _fitness = new();

    private static PerformanceMetrics CreateMetrics(
        int totalTrades = 50,
        double sharpe = 1.5,
        double sortino = 2.0,
        double profitFactor = 2.0,
        double annualizedReturn = 25.0,
        double maxDrawdown = 15.0) => new()
    {
        TotalTrades = totalTrades,
        WinningTrades = totalTrades / 2,
        LosingTrades = totalTrades / 2,
        NetProfit = 5000m,
        GrossProfit = 10000m,
        GrossLoss = -5000m,
        TotalCommissions = 100m,
        TotalReturnPct = 50.0,
        AnnualizedReturnPct = annualizedReturn,
        SharpeRatio = sharpe,
        SortinoRatio = sortino,
        MaxDrawdownPct = maxDrawdown,
        WinRatePct = 50.0,
        ProfitFactor = profitFactor,
        AverageWin = 200.0,
        AverageLoss = -100.0,
        InitialCapital = 10000m,
        FinalEquity = 15000m,
        TradingDays = 252,
    };

    [Fact]
    public void ZeroTrades_ReturnsMinValue()
    {
        var metrics = CreateMetrics(totalTrades: 0);
        Assert.Equal(double.MinValue, _fitness.Evaluate(metrics));
    }

    [Fact]
    public void GoodMetrics_ReturnsPositiveFitness()
    {
        var metrics = CreateMetrics();
        var fitness = _fitness.Evaluate(metrics);
        Assert.True(fitness > 0, $"Expected positive fitness, got {fitness}");
    }

    [Fact]
    public void HighDrawdown_ReducesFitness()
    {
        var normalDD = _fitness.Evaluate(CreateMetrics(maxDrawdown: 20));
        var highDD = _fitness.Evaluate(CreateMetrics(maxDrawdown: 60));
        Assert.True(normalDD > highDD,
            $"Normal DD ({normalDD}) should beat high DD ({highDD})");
    }

    [Fact]
    public void FewTrades_PenalizesFitness()
    {
        var manyTrades = _fitness.Evaluate(CreateMetrics(totalTrades: 50));
        var fewTrades = _fitness.Evaluate(CreateMetrics(totalTrades: 3));
        Assert.True(manyTrades > fewTrades,
            $"Many trades ({manyTrades}) should beat few trades ({fewTrades})");
    }

    [Fact]
    public void NaNMetrics_TreatedAsZero()
    {
        var metrics = CreateMetrics(sharpe: double.NaN, sortino: double.PositiveInfinity);
        var fitness = _fitness.Evaluate(metrics);
        Assert.False(double.IsNaN(fitness));
        Assert.False(double.IsInfinity(fitness));
    }

    [Fact]
    public void HigherSharpe_ProducesHigherFitness()
    {
        var lowSharpe = _fitness.Evaluate(CreateMetrics(sharpe: 0.5));
        var highSharpe = _fitness.Evaluate(CreateMetrics(sharpe: 3.0));
        Assert.True(highSharpe > lowSharpe);
    }

    [Fact]
    public void ProfitFactor_CappedAtFive()
    {
        var pf5 = _fitness.Evaluate(CreateMetrics(profitFactor: 5.0));
        var pf100 = _fitness.Evaluate(CreateMetrics(profitFactor: 100.0));
        // With cap at 5.0, both should contribute the same PF component
        Assert.Equal(pf5, pf100, precision: 10);
    }

    [Fact]
    public void CustomWeights_AffectsResult()
    {
        var weights = new FitnessWeights
        {
            SharpeWeight = 1.0,
            SortinoWeight = 0,
            ProfitFactorWeight = 0,
            AnnualizedReturnWeight = 0,
        };
        var sharpeFocused = new CompositeFitnessFunction(weights);

        var highSharpe = sharpeFocused.Evaluate(CreateMetrics(sharpe: 3.0, sortino: 0));
        var highSortino = sharpeFocused.Evaluate(CreateMetrics(sharpe: 0.5, sortino: 5.0));
        // With all weight on Sharpe, high Sharpe should win
        Assert.True(highSharpe > highSortino);
    }
}
