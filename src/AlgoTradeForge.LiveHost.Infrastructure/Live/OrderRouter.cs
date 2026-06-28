using System.Collections.Concurrent;
using AlgoTradeForge.Domain;
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
    private volatile bool _disposed;

    public IReadOnlyCollection<IAccountTarget> Targets =>
        _targets.Values.Select(e => e.Target).ToList();

    public async Task<IAccountTarget> ResolveTarget(string account, Asset executionAsset, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gate = _gates.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        using var gateLease = await gate.LockAsync(ct);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_targets.TryGetValue(account, out var existing))
        {
            // No post-_disposed re-check here: a cached target stays tracked, so a resolve racing
            // DisposeAsync can't orphan it — at worst the caller gets a target being StopAsync'd,
            // whose Submit/Cancel fail gracefully. Only the create path below can leak, so only it re-checks.
            existing.RefCount++;
            return existing.Target;
        }

        var target = await factory.Create(account, executionAsset, ct);
        var entry = _targets[account] = new Entry(target);

        // Insert-then-verify: DisposeAsync sets _disposed before it enumerates/clears _targets, but
        // without the per-account gate it may snapshot or clear without seeing this just-inserted
        // entry. Re-checking _disposed AFTER the insert closes that window — any interleaving where
        // dispose began is caught here, so no live target is ever orphaned.
        if (_disposed)
        {
            _targets.TryRemove(account, out _);
            await target.DisposeAsync();
            throw new ObjectDisposedException(GetType().FullName);
        }

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
        _disposed = true;
        foreach (var entry in _targets.Values)
        {
            try { await entry.Target.DisposeAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Disposing target {Account} on router shutdown failed", entry.Target.AccountName); }
        }
        _targets.Clear();

        foreach (var gate in _gates.Values)
            gate.Dispose();
        _gates.Clear();
    }
}
