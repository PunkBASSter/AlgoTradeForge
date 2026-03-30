using AlgoTradeForge.Domain.Validation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation;

public class ValidationThresholdProfileTests
{
    [Fact]
    public void CryptoStandard_HasDefaultValues()
    {
        var profile = ValidationThresholdProfile.CryptoStandard();

        Assert.Equal("Crypto-Standard", profile.Name);
        Assert.Equal(1.05, profile.BasicProfitability.MinProfitFactor);
        Assert.Equal(30, profile.BasicProfitability.MinTradeCount);
        Assert.Equal(2.0, profile.BasicProfitability.MinTStatistic);
        Assert.Equal(40.0, profile.BasicProfitability.MaxDrawdownPct);
        Assert.Equal(0.05, profile.StatisticalSignificance.DsrPValue);
        Assert.Equal(0.95, profile.StatisticalSignificance.MinPsr);

        // Stage 6 defaults
        Assert.Equal(1000, profile.MonteCarloPermutation.BootstrapIterations);
        Assert.Equal(1.5, profile.MonteCarloPermutation.MaxDrawdownMultiplier);
        Assert.Equal(1000, profile.MonteCarloPermutation.PermutationIterations);
        Assert.Equal(0.05, profile.MonteCarloPermutation.MaxPermutationPValue);
        Assert.Equal(2.0, profile.MonteCarloPermutation.CostStressMultiplier);

        // Stage 7 defaults
        Assert.Equal(16, profile.SelectionBiasAudit.CscvBlocks);
        Assert.Equal(0.30, profile.SelectionBiasAudit.MaxPbo);
        Assert.Equal(0.70, profile.SelectionBiasAudit.MinProfitableSubPeriods);
        Assert.Equal(0.85, profile.SelectionBiasAudit.MinR2);
        Assert.Equal(8, profile.SelectionBiasAudit.SubPeriodCount);
        Assert.Equal(60, profile.SelectionBiasAudit.RollingSharpeWindow);
        Assert.Equal(-0.001, profile.SelectionBiasAudit.MaxSharpeDecaySlope);
        Assert.Equal(60, profile.SelectionBiasAudit.RegimeVolWindow);
    }

    [Fact]
    public void CryptoConservative_HasStricterThresholds()
    {
        var profile = ValidationThresholdProfile.CryptoConservative();

        Assert.Equal("Crypto-Conservative", profile.Name);
        Assert.Equal(1.10, profile.BasicProfitability.MinProfitFactor);
        Assert.Equal(50, profile.BasicProfitability.MinTradeCount);
        Assert.Equal(30.0, profile.BasicProfitability.MaxDrawdownPct);
        Assert.Equal(0.01, profile.StatisticalSignificance.DsrPValue);
        Assert.Equal(0.99, profile.StatisticalSignificance.MinPsr);
        Assert.Equal(0.60, profile.WalkForwardOptimization.MinWfe);
        Assert.Equal(0.80, profile.WalkForwardOptimization.MinProfitableWindowsPct);
        Assert.Equal(0.60, profile.WalkForwardMatrix.MinWfe);
        Assert.Equal(0.25, profile.ParameterLandscape.MaxDegradationPct);
        Assert.Equal(0.60, profile.ParameterLandscape.MinClusterConcentration);

        // Stage 6 conservative overrides
        Assert.Equal(2000, profile.MonteCarloPermutation.BootstrapIterations);
        Assert.Equal(1.3, profile.MonteCarloPermutation.MaxDrawdownMultiplier);
        Assert.Equal(2000, profile.MonteCarloPermutation.PermutationIterations);
        Assert.Equal(0.01, profile.MonteCarloPermutation.MaxPermutationPValue);
        Assert.Equal(2.5, profile.MonteCarloPermutation.CostStressMultiplier);

        // Stage 7 conservative overrides
        Assert.Equal(0.20, profile.SelectionBiasAudit.MaxPbo);
        Assert.Equal(0.80, profile.SelectionBiasAudit.MinProfitableSubPeriods);
        Assert.Equal(0.90, profile.SelectionBiasAudit.MinR2);
    }

    [Fact]
    public void CryptoStandard_HasStage3To5Defaults()
    {
        var profile = ValidationThresholdProfile.CryptoStandard();

        Assert.Equal(0.30, profile.ParameterLandscape.MaxDegradationPct);
        Assert.Equal(0.50, profile.ParameterLandscape.MinClusterConcentration);
        Assert.Equal(0.10, profile.ParameterLandscape.SensitivityRange);
        Assert.Equal(0.50, profile.WalkForwardOptimization.MinWfe);
        Assert.Equal(0.70, profile.WalkForwardOptimization.MinProfitableWindowsPct);
        Assert.Equal(0.20, profile.WalkForwardOptimization.OosPct);
        Assert.Equal(6, profile.WalkForwardMatrix.PeriodCounts.Length);
        Assert.Equal(3, profile.WalkForwardMatrix.OosPcts.Length);
        Assert.Equal(0.50, profile.WalkForwardMatrix.MinWfe);
    }

    [Fact]
    public void GetByName_Standard_Resolves()
    {
        var profile = ValidationThresholdProfile.GetByName("Crypto-Standard");
        Assert.Equal("Crypto-Standard", profile.Name);
    }

    [Fact]
    public void GetByName_Conservative_Resolves()
    {
        var profile = ValidationThresholdProfile.GetByName("Crypto-Conservative");
        Assert.Equal("Crypto-Conservative", profile.Name);
    }

    [Fact]
    public void GetByName_Unknown_Throws()
    {
        Assert.Throws<ArgumentException>(() => ValidationThresholdProfile.GetByName("Unknown"));
    }
}
