using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.Modules.Exit;

/// <summary>
/// Exits position when market regime has changed from the regime at entry time.
/// Returns -80 when regime changed, 0 when same or when either regime is Unknown.
/// Tracks per-group entry regimes.
/// </summary>
public sealed class RegimeChangeExitRule : IExitRule
{
    private readonly Dictionary<long, MarketRegime> _entryRegimes = [];

    public string Name => "RegimeChange";

    /// <summary>
    /// Records the regime at entry time for a given order group.
    /// </summary>
    public void Activate(long groupId, MarketRegime entryRegime)
        => _entryRegimes[groupId] = entryRegime;

    /// <summary>
    /// Removes tracking for a closed group.
    /// </summary>
    public void Remove(long groupId)
        => _entryRegimes.Remove(groupId);

    public int Evaluate(Int64Bar bar, StrategyContext context, OrderGroup group)
    {
        if (!_entryRegimes.TryGetValue(group.GroupId, out var entryRegime))
            return 0;

        if (entryRegime == MarketRegime.Unknown || context.CurrentRegime == MarketRegime.Unknown)
            return 0;

        return context.CurrentRegime != entryRegime ? -80 : 0;
    }
}
