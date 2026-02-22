using System.Collections.Concurrent;

namespace AlgoTradeForge.Application.Progress;

public sealed class InMemoryRunCancellationRegistry : IRunCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _registry = new();

    public void Register(Guid id, CancellationTokenSource cts)
    {
        _registry[id] = cts;
    }

    public bool TryCancel(Guid id)
    {
        if (!_registry.TryGetValue(id, out var cts))
            return false;

        cts.Cancel();
        return true;
    }

    public CancellationToken? TryGetToken(Guid id)
    {
        return _registry.TryGetValue(id, out var cts) ? cts.Token : null;
    }

    public void Remove(Guid id)
    {
        if (_registry.TryRemove(id, out var cts))
            cts.Dispose();
    }
}
