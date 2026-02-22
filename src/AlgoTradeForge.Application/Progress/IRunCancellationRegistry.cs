namespace AlgoTradeForge.Application.Progress;

public interface IRunCancellationRegistry
{
    void Register(Guid id, CancellationTokenSource cts);
    bool TryCancel(Guid id);
    CancellationToken? TryGetToken(Guid id);
    void Remove(Guid id);
}
