namespace AlgoTradeForge.Domain.Events;

public sealed class NullEventBus : IEventBus
{
    public static readonly NullEventBus Instance = new();
    private NullEventBus() { }

    public void Emit<T>(T evt) where T : IBacktestEvent { }
}
