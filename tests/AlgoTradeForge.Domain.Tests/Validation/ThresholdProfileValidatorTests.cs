using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Validation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation;

public class ThresholdProfileValidatorTests
{
    [Fact]
    public void ValidProfile_NoViolations()
    {
        var profile = ValidationThresholdProfile.CryptoStandard();
        var violations = ThresholdProfileValidator.Validate(profile);
        Assert.Empty(violations);
    }

    [Fact]
    public void ConservativeProfile_NoViolations()
    {
        var profile = ValidationThresholdProfile.CryptoConservative();
        var violations = ThresholdProfileValidator.Validate(profile);
        Assert.Empty(violations);
    }

    [Fact]
    public void TradeCountBelowFloor_ReportsViolation()
    {
        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            BasicProfitability = new ValidationThresholdProfile.Stage1BasicProfitabilityThresholds
            {
                MinTradeCount = 5, // Below safety floor of 30
            },
        };

        var violations = ThresholdProfileValidator.Validate(profile);
        Assert.Single(violations);
        Assert.Contains("MinTradeCount", violations[0]);
    }

    [Fact]
    public void PboAboveFloor_ReportsViolation()
    {
        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            SelectionBiasAudit = new ValidationThresholdProfile.Stage7SelectionBiasAuditThresholds
            {
                MaxPbo = 0.70, // Above safety floor of 0.60
            },
        };

        var violations = ThresholdProfileValidator.Validate(profile);
        Assert.Single(violations);
        Assert.Contains("MaxPbo", violations[0]);
    }

    [Fact]
    public void WfeBelowFloor_ReportsViolation()
    {
        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            WalkForwardOptimization = new ValidationThresholdProfile.Stage4WalkForwardOptimizationThresholds
            {
                MinWfe = 0.10, // Below safety floor of 0.30
            },
        };

        var violations = ThresholdProfileValidator.Validate(profile);
        Assert.Single(violations);
        Assert.Contains("MinWfe", violations[0]);
    }

    [Fact]
    public void MultipleViolations_ReportsAll()
    {
        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            BasicProfitability = new ValidationThresholdProfile.Stage1BasicProfitabilityThresholds
            {
                MinTradeCount = 5,
            },
            SelectionBiasAudit = new ValidationThresholdProfile.Stage7SelectionBiasAuditThresholds
            {
                MaxPbo = 0.80,
            },
            WalkForwardOptimization = new ValidationThresholdProfile.Stage4WalkForwardOptimizationThresholds
            {
                MinWfe = 0.10,
            },
        };

        var violations = ThresholdProfileValidator.Validate(profile);
        Assert.Equal(3, violations.Count);
    }

    [Fact]
    public void CustomFloors_Enforced()
    {
        var profile = ValidationThresholdProfile.CryptoStandard();
        var strictFloors = new ValidationThresholdProfile.SafetyFloorThresholds
        {
            MinTradeCount = 100, // Stricter than default 30
        };

        var violations = ThresholdProfileValidator.Validate(profile, strictFloors);
        Assert.Single(violations);
        Assert.Contains("MinTradeCount", violations[0]);
    }
}
