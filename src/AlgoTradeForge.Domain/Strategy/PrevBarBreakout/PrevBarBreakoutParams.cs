using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.PrevBarBreakout;

public sealed class PrevBarBreakoutParams : ModularStrategyParamsBase
{
    [Optimizable(Min = 0, Max = 20, Step = 5)]
    public long EntryOffsetTicks { get; init; }

    [Optimizable(Min = 0, Max = 20, Step = 5)]
    public long SlBufferTicks { get; init; }

    [Optimizable(Min = 1, Max = 50, Step = 1)]
    public int MaxBars { get; init; } = 5;

    [Optimizable(Min = 4, Max = 30, Step = 2)]
    public int AtrPeriod { get; init; } = 14;

    /// <summary>
    /// Minimum ATR/prev-bar-close ratio (in percent) required to take a new entry.
    /// 0 disables the filter (the default — strategy enters on every bar). 0.5 means
    /// "ATR must be at least 0.5% of the previous bar's close price."
    /// </summary>
    [Optimizable(Min = 0.0, Max = 2.0, Step = 0.25)]
    public double MinVolatilityPct { get; init; }

    public override IMoneyManagementModule MoneyManagement { get; init; } =
        new FixedNotionalModule(new FixedNotionalParams { Notional = 1000_00 });

    // High cap so the symmetric Buy+Sell stop pair is never rejected by the registry.
    public override TradeRegistryParams TradeRegistry { get; init; } =
        new() { MaxConcurrentGroups = 32 };
}
