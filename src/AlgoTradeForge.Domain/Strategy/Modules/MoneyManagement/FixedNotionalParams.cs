using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

public sealed class FixedNotionalParams : ModuleParamsBase
{
    /// <summary>Fixed notional per trade in quote-asset units (int64-encoded).
    /// Each trade allocates exactly this dollar amount regardless of risk distance,
    /// enabling cross-asset comparable optimization (e.g. $1000 of BTC = $1000 of ADA).</summary>
    public long Notional { get; init; } = 10_000;
}
