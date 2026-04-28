using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

/// <summary>
/// Sizes positions as a fixed notional amount in quote-asset units.
/// qty = notional / entryPrice — independent of risk distance.
/// </summary>
[ModuleKey("mm.fixed-notional")]
public sealed class FixedNotionalModule(FixedNotionalParams parameters)
    : MoneyManagementModuleBase, IStrategyModule<FixedNotionalParams>
{
    protected override decimal CalculateRawQuantity(
        long equity, long entryPrice, long stopLoss, long riskDistance, StrategyContextBase context)
    {
        if (entryPrice <= 0) return 0m;
        return (decimal)parameters.Notional / entryPrice;
    }
}
