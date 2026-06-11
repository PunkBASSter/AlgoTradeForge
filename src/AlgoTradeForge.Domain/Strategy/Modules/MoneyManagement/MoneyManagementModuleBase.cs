using AlgoTradeForge.Domain.Events;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

/// <summary>
/// Template Method base for all money management modules.
/// Handles common guard checks and post-processing (rounding, clamping, min-check),
/// holds the module's typed <see cref="Params"/>, and implements
/// <see cref="IStrategyModule{TParams}"/> so subclasses declare only the raw sizing math.
///
/// A positive computed size below the exchange minimum is clamped UP to
/// <see cref="Asset.MinOrderQuantity"/> rather than dropped: the smallest executable
/// lot keeps every asset tradeable regardless of price level, and settlement
/// validation still rejects genuinely unaffordable orders. Sizing outcome changes
/// (clamped / zero / recovered) are emitted as warning events once per transition,
/// so a misconfigured notional is visible in the event log instead of silently
/// producing months without orders.
/// </summary>
public abstract class MoneyManagementModuleBase<TParams>(TParams parameters)
    : IMoneyManagementModule, IStrategyModule<TParams>, IEventBusReceiver
    where TParams : ModuleParamsBase
{
    private IEventBus _bus = NullEventBus.Instance;
    private readonly Dictionary<string, SizingOutcome> _lastOutcome = [];

    private enum SizingOutcome { Normal, ClampedToMin, Zero }

    protected TParams Params { get; } = parameters;

    public virtual ModuleParamsBase? ModuleParams => Params;

    public void SetEventBus(IEventBus bus) => _bus = bus;

    public decimal CalculateSize(long entryPrice, long stopLoss, StrategyContextBase context, Asset asset)
    {
        if (entryPrice == 0 || stopLoss == 0) return 0m;

        var riskDistance = Math.Abs(entryPrice - stopLoss);
        if (riskDistance == 0) return 0m;

        var equity = context.Equity;
        if (equity <= 0)
            return Report(asset, context, SizingOutcome.Zero, 0m,
                $"equity {equity} is non-positive");

        var rawQty = CalculateRawQuantity(equity, entryPrice, stopLoss, riskDistance, context);

        if (rawQty <= 0)
            return Report(asset, context, SizingOutcome.Zero, 0m,
                $"raw quantity {rawQty} is non-positive");

        var qty = asset.RoundQuantityDown(rawQty);
        var minExecutable = Math.Max(asset.MinOrderQuantity, asset.QuantityStepSize);
        if (qty < minExecutable)
        {
            qty = Math.Min(minExecutable, asset.MaxOrderQuantity);
            return Report(asset, context, SizingOutcome.ClampedToMin, qty,
                $"computed quantity {rawQty} below minimum {minExecutable} at entry {entryPrice}; clamped to smallest executable lot");
        }

        qty = Math.Clamp(qty, 0m, asset.MaxOrderQuantity);
        return Report(asset, context, SizingOutcome.Normal, qty, null);
    }

    /// <summary>
    /// Compute raw (unrounded, unclamped) position size.
    /// Called only when equity > 0, riskDistance > 0, and both prices are non-zero.
    /// </summary>
    protected abstract decimal CalculateRawQuantity(
        long equity, long entryPrice, long stopLoss, long riskDistance, StrategyContextBase context);

    private decimal Report(
        Asset asset, StrategyContextBase context, SizingOutcome outcome, decimal qty, string? detail)
    {
        var hadPrevious = _lastOutcome.TryGetValue(asset.Name, out var previous);
        if (hadPrevious && previous == outcome)
            return qty;
        _lastOutcome[asset.Name] = outcome;

        // Initial normal sizing is the expected state, not a transition worth reporting
        if (!hadPrevious && outcome == SizingOutcome.Normal)
            return qty;

        var message = outcome switch
        {
            SizingOutcome.ClampedToMin => $"Sizing for {asset.Name}: {detail}",
            SizingOutcome.Zero => $"Sizing for {asset.Name} returned 0 ({detail}); entries are skipped",
            _ => $"Sizing for {asset.Name} recovered (quantity {qty})",
        };

        _bus.Emit(new WarningEvent(context.CurrentBar.Timestamp, EventSources.MoneyManagement, message));
        return qty;
    }
}
