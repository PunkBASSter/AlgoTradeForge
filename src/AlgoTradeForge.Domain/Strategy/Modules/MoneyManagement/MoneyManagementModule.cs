using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

[ModuleKey("money-management")]
public sealed class MoneyManagementModule(MoneyManagementParams parameters)
    : IStrategyModule<MoneyManagementParams>
{
    public decimal CalculateSize(long entryPrice, long stopLoss, StrategyContext context, Asset asset)
    {
        if (entryPrice == 0 || stopLoss == 0) return 0m;

        var riskDistance = Math.Abs(entryPrice - stopLoss);
        if (riskDistance == 0) return 0m;

        var equity = context.Equity;
        if (equity <= 0) return 0m;

        var rawQty = parameters.Method switch
        {
            SizingMethod.FixedFractional => CalculateFixedFractional(equity, riskDistance),
            SizingMethod.AtrVolTarget => CalculateAtrVolTarget(equity, context),
            SizingMethod.HalfKelly => CalculateHalfKelly(equity, entryPrice),
            _ => 0m
        };

        if (rawQty <= 0) return 0m;

        var qty = asset.RoundQuantityDown(rawQty);
        qty = Math.Clamp(qty, 0m, asset.MaxOrderQuantity);

        return qty < asset.MinOrderQuantity ? 0m : qty;
    }

    private decimal CalculateFixedFractional(long equity, long riskDistance)
    {
        // qty = (equity * riskPercent) / riskDistance
        var riskAmount = equity * parameters.RiskPercent / 100.0;
        return (decimal)(riskAmount / riskDistance);
    }

    private decimal CalculateAtrVolTarget(long equity, StrategyContext context)
    {
        var atr = context.CurrentAtr;
        if (atr <= 0) return 0m;

        // qty = (equity * volTarget) / ATR
        var targetNotional = equity * parameters.VolTarget;
        return (decimal)(targetNotional / atr);
    }

    private decimal CalculateHalfKelly(long equity, long entryPrice)
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
