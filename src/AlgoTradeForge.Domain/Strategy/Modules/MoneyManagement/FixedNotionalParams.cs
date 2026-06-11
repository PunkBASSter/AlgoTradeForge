using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

public sealed class FixedNotionalParams : ModuleParamsBase
{
    /// <summary>Fixed notional per trade in quote-asset units (int64-encoded).
    /// Each trade allocates exactly this dollar amount regardless of risk distance,
    /// enabling cross-asset comparable optimization (e.g. $1000 of BTC = $1000 of ADA).
    /// Default is $1,000: comfortably above the min-lot notional of every catalogued
    /// asset (e.g. 0.001 BTC ≈ $100), so default-configured runs never sit at the
    /// clamp threshold.</summary>
    public long Notional { get; init; } = 100_000;
}
