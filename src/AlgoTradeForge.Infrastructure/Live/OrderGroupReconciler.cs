using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.Live;

public sealed class OrderGroupReconciler(IExchangeOrderClient orderClient, ILogger logger)
{
    public sealed record ReconciliationResult(
        Dictionary<long, HashSet<long>> MissingByGroup,
        List<long> OrphanIds);

    /// <summary>
    /// Phase 1: Exchange query + diff. No module mutation — safe to call from any thread.
    /// </summary>
    public async Task<ReconciliationResult> DetectAsync(
        string symbol,
        IReadOnlyList<ExpectedOrder> expected,
        Func<long, long> resolveExchangeId,
        IReadOnlySet<long>? knownPendingIds,
        CancellationToken ct)
    {
        var missingByGroup = new Dictionary<long, HashSet<long>>();
        var orphanIds = new List<long>();

        if (expected.Count == 0)
            return new ReconciliationResult(missingByGroup, orphanIds);

        var translated = expected
            .Select(e => (Expected: e, ExchangeId: resolveExchangeId(e.OrderId)))
            .Where(e => e.ExchangeId > 0) // only orders that reached the exchange
            .ToList();

        var exchangeOrders = await orderClient.GetOpenOrdersAsync(symbol, ct);
        var exchangeIds = new HashSet<long>(exchangeOrders.Select(o => o.OrderId));

        // 1. Missing orders
        foreach (var (exp, exchId) in translated)
        {
            if (!exchangeIds.Contains(exchId))
            {
                logger.LogWarning(
                    "Reconciler: Missing {Type} order {OrderId} (exchange {ExchangeId}) for group {GroupId}",
                    exp.Type, exp.OrderId, exchId, exp.GroupId);

                if (!missingByGroup.TryGetValue(exp.GroupId, out var set))
                {
                    set = [];
                    missingByGroup[exp.GroupId] = set;
                }
                set.Add(exp.OrderId); // local ID for RepairGroup
            }
        }

        // 2. Orphaned orders — exclude known pending (non-TradeRegistry) orders
        var expectedExchangeIds = new HashSet<long>(translated.Select(e => e.ExchangeId));
        foreach (var orphan in exchangeOrders)
        {
            if (!expectedExchangeIds.Contains(orphan.OrderId)
                && (knownPendingIds is null || !knownPendingIds.Contains(orphan.OrderId)))
            {
                orphanIds.Add(orphan.OrderId);
            }
        }

        return new ReconciliationResult(missingByGroup, orphanIds);
    }

    /// <summary>
    /// Phase 2: Cancel orphans directly on exchange. No module state involved.
    /// </summary>
    public async Task CancelOrphansAsync(string symbol, IReadOnlyList<long> orphanIds, CancellationToken ct)
    {
        foreach (var orphanId in orphanIds)
        {
            logger.LogWarning("Reconciler: Orphaned order {OrderId} on exchange. Cancelling.", orphanId);
            try
            {
                await orderClient.CancelOrderAsync(symbol, orphanId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to cancel orphaned order {OrderId}", orphanId);
            }
        }
    }

    /// <summary>
    /// Convenience: single-threaded reconciliation (tests, backtest).
    /// Combines detect + repair + cancel in one call.
    /// </summary>
    public async Task ReconcileAsync(
        string symbol,
        TradeRegistryModule registry,
        Func<long, long> resolveExchangeId,
        IOrderContext orders,
        CancellationToken ct)
    {
        var expected = registry.GetExpectedOrders();
        var result = await DetectAsync(symbol, expected, resolveExchangeId, knownPendingIds: null, ct);

        foreach (var (groupId, missingIds) in result.MissingByGroup)
            registry.RepairGroup(groupId, missingIds, orders);

        if (result.OrphanIds.Count > 0)
            await CancelOrphansAsync(symbol, result.OrphanIds, ct);
    }
}
