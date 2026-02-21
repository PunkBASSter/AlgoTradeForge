namespace AlgoTradeForge.Domain.Events;

public interface IEventBus
{
    void Emit<T>(T evt) where T : IBacktestEvent;

    /// <summary>Whether mutation events (e.g. bar.mut, ind.mut) are currently emitted.</summary>
    bool MutationsEnabled => false;

    /// <summary>Toggle emission of mutation events at runtime.</summary>
    void SetMutationsEnabled(bool enabled) { }
}
