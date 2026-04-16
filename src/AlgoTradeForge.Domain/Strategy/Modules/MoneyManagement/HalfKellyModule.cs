using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

[ModuleKey("mm.half-kelly")]
public sealed class HalfKellyModule(HalfKellyParams parameters)
    : MoneyManagementModuleBase, IStrategyModule<HalfKellyParams>
{
    protected override decimal CalculateRawQuantity(
        long equity, long entryPrice, long stopLoss, long riskDistance, StrategyContextBase context)
    {
        if (entryPrice <= 0) return 0m;

        // Kelly fraction: f = (winRate * payoffRatio - (1 - winRate)) / payoffRatio
        var kellyF = (parameters.WinRate * parameters.PayoffRatio - (1 - parameters.WinRate))
                     / parameters.PayoffRatio;
        if (kellyF <= 0) return 0m;

        // Half-Kelly for safety: qty = 0.5 * f * equity / price
        var halfKellyFraction = 0.5 * kellyF;
        return (decimal)(halfKellyFraction * equity / entryPrice);
    }
}
