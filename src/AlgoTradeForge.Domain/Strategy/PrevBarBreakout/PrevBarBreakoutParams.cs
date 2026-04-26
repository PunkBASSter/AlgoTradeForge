using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

namespace AlgoTradeForge.Domain.Strategy.PrevBarBreakout;

public sealed class PrevBarBreakoutParams : ModularStrategyParamsBase
{
    /// <summary>
    /// Ticks added to the just-closed bar's high (Buy stop) and subtracted from its low
    /// (Sell stop) to set the entry trigger price. MUST be ≥ 1 for the strategy to place
    /// any pendings — with offset 0 the stop would sit exactly at bar.High / bar.Low and
    /// the engine's post-OnBarComplete same-bar fill loop would trigger it immediately,
    /// which the strategy guards against by skipping placement. The optimizable range
    /// includes 0 so the optimizer can sample the degenerate "no trades" point if needed,
    /// but the property default is 1 so out-of-the-box runs actually trade.
    /// </summary>
    [Optimizable(Min = 0, Max = 20, Step = 5)]
    public long EntryOffsetTicks { get; init; } = 1;

    [Optimizable(Min = 0, Max = 20, Step = 5)]
    public long SlBufferTicks { get; init; } = 1;

    /// <summary>
    /// Number of bars to hold the position past the entry's fill bar. <c>0</c> closes on the
    /// fill bar's close (1-bar momentum); <c>N</c> holds N bars beyond, closing on the
    /// (fillBar + N)-th bar's close. Measured from <see cref="OrderGroup.EntryFilledAt"/> —
    /// the registry's actual fill timestamp — so the value matches the intuitive reading.
    /// </summary>
    [Optimizable(Min = 0, Max = 24, Step = 1)]
    public int MaxBars { get; init; } = 1;

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

    // Cap = 2 admits the symmetric Buy+Sell pending pair but no more.
    public override TradeRegistryParams TradeRegistry { get; init; } =
        new() { MaxConcurrentGroups = 2 };
}
