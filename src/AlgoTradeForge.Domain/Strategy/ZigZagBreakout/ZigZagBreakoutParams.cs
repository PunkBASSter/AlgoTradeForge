using AlgoTradeForge.Domain.Optimization.Attributes;

namespace AlgoTradeForge.Domain.Strategy.ZigZagBreakout;

public class ZigZagBreakoutParams : StrategyParamsBase
{
    [Optimizable(Min = 1, Max = 20, Step = 0.5)]
    public decimal DzzDepth { get; init; } = 5m;

    [Optimizable(Min = 50, Max = 500, Step = 50, Unit = ParamUnit.QuoteAsset)]
    public long MinimumThreshold { get; init; } = 10_000L;

    [Optimizable(Min = 0.5, Max = 3, Step = 0.5)]
    public decimal RiskPercentPerTrade { get; init; } = 1m;

    [Optimizable(Min = 5, Max = 50, Step = 1)]
    public int AtrPeriod { get; init; } = 14;

    /// <summary>Minimum ATR in quote-asset units. 0 means no minimum.</summary>
    [Optimizable(Min = 0, Max = 50, Step = 0.5, Unit = ParamUnit.QuoteAsset)]
    public long AtrMin { get; init; } = 0;

    /// <summary>Maximum ATR in quote-asset units. 0 means no maximum.</summary>
    [Optimizable(Min = 0, Max = 500, Step = 5, Unit = ParamUnit.QuoteAsset)]
    public long AtrMax { get; init; } = 0;
}
