using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.Application.Indicators;

/// <summary>
/// Wraps each indicator with <see cref="EmittingIndicatorDecorator{TInp,TBuff}"/> so that
/// <c>ind</c> and <c>ind.mut</c> events are emitted automatically on every <c>Compute</c> call.
/// Used during debug and backtest runs.
/// </summary>
public sealed class EmittingIndicatorFactory(IEventBus bus) : IIndicatorFactory
{
    public IIndicator<TInp, TBuff> Create<TInp, TBuff>(
        IIndicator<TInp, TBuff> indicator,
        DataSubscription subscription)
        => new EmittingIndicatorDecorator<TInp, TBuff>(indicator, bus, subscription);
}
