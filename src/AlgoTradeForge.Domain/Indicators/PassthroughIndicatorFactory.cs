using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Domain.Indicators;

/// <summary>
/// No-op factory that returns the indicator unchanged. Used during optimization runs
/// to avoid decorator overhead when event emission is not needed.
/// </summary>
public sealed class PassthroughIndicatorFactory : IIndicatorFactory
{
    public static readonly PassthroughIndicatorFactory Instance = new();
    private PassthroughIndicatorFactory() { }

    public IIndicator<TInp, TBuff> Create<TInp, TBuff>(
        IIndicator<TInp, TBuff> indicator,
        DataSubscription subscription) => indicator;
}
