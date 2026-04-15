using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

[ModuleKey("mm.atr-vol-target")]
public sealed class AtrVolTargetModule(AtrVolTargetParams parameters)
    : MoneyManagementModuleBase, IStrategyModule<AtrVolTargetParams>
{
    protected override decimal CalculateRawQuantity(
        long equity, long entryPrice, long stopLoss, long riskDistance, StrategyContext context)
    {
        var atr = context.CurrentAtr;
        if (atr <= 0) return 0m;

        // qty = (equity * volTarget) / ATR
        var targetNotional = equity * parameters.VolTarget;
        return (decimal)(targetNotional / atr);
    }
}
