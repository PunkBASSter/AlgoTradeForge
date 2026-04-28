using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

[ModuleKey("mm.fixed-fractional")]
public sealed class FixedFractionalModule(FixedFractionalParams parameters)
    : MoneyManagementModuleBase, IStrategyModule<FixedFractionalParams>
{
    protected override decimal CalculateRawQuantity(
        long equity, long entryPrice, long stopLoss, long riskDistance, StrategyContextBase context)
    {
        // qty = (equity * riskPercent%) / riskDistance
        var riskAmount = equity * parameters.RiskPercent / 100.0;
        return (decimal)(riskAmount / riskDistance);
    }
}
