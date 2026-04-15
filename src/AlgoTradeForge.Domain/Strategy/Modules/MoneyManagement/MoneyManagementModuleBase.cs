namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

/// <summary>
/// Template Method base for all money management modules.
/// Handles common guard checks and post-processing (rounding, clamping, min-check).
/// Subclasses implement only the raw sizing math.
/// </summary>
public abstract class MoneyManagementModuleBase : IMoneyManagementModule
{
    public decimal CalculateSize(long entryPrice, long stopLoss, StrategyContext context, Asset asset)
    {
        if (entryPrice == 0 || stopLoss == 0) return 0m;

        var riskDistance = Math.Abs(entryPrice - stopLoss);
        if (riskDistance == 0) return 0m;

        var equity = context.Equity;
        if (equity <= 0) return 0m;

        var rawQty = CalculateRawQuantity(equity, entryPrice, stopLoss, riskDistance, context);

        if (rawQty <= 0) return 0m;

        var qty = asset.RoundQuantityDown(rawQty);
        qty = Math.Clamp(qty, 0m, asset.MaxOrderQuantity);

        return qty < asset.MinOrderQuantity ? 0m : qty;
    }

    /// <summary>
    /// Compute raw (unrounded, unclamped) position size.
    /// Called only when equity > 0, riskDistance > 0, and both prices are non-zero.
    /// </summary>
    protected abstract decimal CalculateRawQuantity(
        long equity, long entryPrice, long stopLoss, long riskDistance, StrategyContext context);
}
