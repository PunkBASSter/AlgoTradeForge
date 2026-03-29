using AlgoTradeForge.Domain.Validation;

namespace AlgoTradeForge.Application.Validation;

/// <summary>
/// Validates a <see cref="ValidationThresholdProfile"/> against safety floor constraints.
/// Safety floors are hard minimums/maximums that cannot be relaxed regardless of profile.
/// </summary>
public static class ThresholdProfileValidator
{
    public static IReadOnlyList<string> Validate(
        ValidationThresholdProfile profile,
        ValidationThresholdProfile.SafetyFloorThresholds? floors = null)
    {
        floors ??= new ValidationThresholdProfile.SafetyFloorThresholds();
        var violations = new List<string>();

        if (profile.BasicProfitability.MinTradeCount < floors.MinTradeCount)
            violations.Add(
                $"MinTradeCount ({profile.BasicProfitability.MinTradeCount}) is below safety floor ({floors.MinTradeCount}).");

        if (profile.SelectionBiasAudit.MaxPbo > floors.MaxPbo)
            violations.Add(
                $"MaxPbo ({profile.SelectionBiasAudit.MaxPbo:F2}) exceeds safety floor ({floors.MaxPbo:F2}).");

        if (profile.WalkForwardOptimization.MinWfe < floors.MinWfe)
            violations.Add(
                $"MinWfe ({profile.WalkForwardOptimization.MinWfe:F2}) is below safety floor ({floors.MinWfe:F2}).");

        return violations;
    }
}
