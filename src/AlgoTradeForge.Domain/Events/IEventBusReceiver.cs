namespace AlgoTradeForge.Domain.Events;

public interface IEventBusReceiver
{
    void SetEventBus(IEventBus bus);
}
