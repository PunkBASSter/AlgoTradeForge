using System.Collections.Concurrent;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

public sealed class OrderRouter(IAccountTargetFactory factory, ILogger<OrderRouter> logger) : IOrderRouter
{
    private sealed class Entry(IAccountTarget target) { public readonly IAccountTarget Target = target; public int RefCount; }

    private readonly ConcurrentDictionary<string, Entry> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, Guid> _orderToSession = new();

    public IReadOnlyCollection<IAccountTarget> Targets =>
        _targets.Values.Select(e => e.Target).ToList();

    public async Task<IAccountTarget> ResolveTarget(string account, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        using var _ = await gate.LockAsync(ct);

        var entry = _targets.TryGetValue(account, out var existing)
            ? existing
            : _targets[account] = new Entry(await factory.Create(account, ct));

        entry.RefCount++;
        return entry.Target;
    }

    public async Task ReleaseTarget(string account, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        using var gateLease = await gate.LockAsync(ct);

        if (!_targets.TryGetValue(account, out var entry))
            return;

        if (--entry.RefCount > 0)
            return;

        _targets.TryRemove(account, out _);
        try { await entry.Target.DisposeAsync(); }
        catch (Exception ex) { logger.LogError(ex, "Disposing target for account {Account} failed", account); }
    }

    public void TrackOrder(long exchangeOrderId, Guid sessionId) =>
        _orderToSession[exchangeOrderId] = sessionId;

    public void UntrackOrder(long exchangeOrderId) =>
        _orderToSession.TryRemove(exchangeOrderId, out _);

    public bool TryResolveSession(long exchangeOrderId, out Guid sessionId) =>
        _orderToSession.TryGetValue(exchangeOrderId, out sessionId);

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _targets.Values)
        {
            try { await entry.Target.DisposeAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Disposing target {Account} on router shutdown failed", entry.Target.AccountName); }
        }
        _targets.Clear();
    }
}
