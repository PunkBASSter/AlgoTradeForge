namespace AlgoTradeForge.Domain.Events;

public interface IEventBus
{
    void Emit<T>(T evt) where T : IBacktestEvent;
}
