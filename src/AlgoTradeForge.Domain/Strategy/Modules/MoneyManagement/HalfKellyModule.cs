using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

[ModuleKey("mm.half-kelly")]
public sealed class HalfKellyModule(HalfKellyParams parameters)
    : MoneyManagementModuleBase<HalfKellyParams>(parameters)
{
    protected override decimal CalculateRawQuantity(
        long equity, long entryPrice, long stopLoss, long riskDistance, StrategyContextBase context)
    {
        if (entryPrice <= 0) return 0m;

        // Kelly fraction: f = (winRate * payoffRatio - (1 - winRate)) / payoffRatio
        var kellyF = (Params.WinRate * Params.PayoffRatio - (1 - Params.WinRate))
                     / Params.PayoffRatio;
        if (kellyF <= 0) return 0m;

        // Half-Kelly for safety: qty = 0.5 * f * equity / price
        var halfKellyFraction = 0.5 * kellyF;
        return (decimal)(halfKellyFraction * equity / entryPrice);
    }
}
