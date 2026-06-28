using System.Collections.Concurrent;
using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Venue-neutral per-session dispatch core extracted from BinanceLiveConnector. Owns the session
// table, the exec-priority queue drain, the reconciliation loop, and report routing. Venue
// connectors (Binance, IB) compose it: they own transport + the raw→neutral report mapping +
// quote-asset resolution, and delegate the session lifecycle + order side here.
public sealed class LiveSessionDispatcher
{
    private readonly IOrderRouter _router;
    private readonly IMarketDataSource _source;
    private readonly IStrategyDispatch _dispatch;
    private readonly OrderGroupReconciler _reconciler;
    private readonly LiveDispatcherOptions _options;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<Guid, LiveSessionEntry> _sessions = new();
    private readonly ConcurrentDictionary<long, ConcurrentQueue<ExecutionReport>> _bufferedReports = new();

    // Sessions removed while their account stays alive (co-tenant). An in-flight order a removed
    // session submitted before removal is placed by the shared order context after removal; when it
    // re-keys (OrderMapped), we cancel it so no order rests under an already-removed session.
    private readonly ConcurrentDictionary<Guid, byte> _removedSessions = new();

    private CancellationTokenSource? _cts;
    private Task? _reconcileTask;

    public LiveSessionDispatcher(
        IOrderRouter router,
        IMarketDataSource source,
        IStrategyDispatch dispatch,
        OrderGroupReconciler reconciler,
        LiveDispatcherOptions options,
        ILogger logger)
    {
        _router = router;
        _source = source;
        _dispatch = dispatch;
        _reconciler = reconciler;
        _options = options;
        _logger = logger;
    }

    public IReadOnlyCollection<Guid> SessionIds => _sessions.Keys.ToList();

    public void Start(CancellationToken ct) =>
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

    // Runs on the dispatcher's OWN linked token (set by Start), so Stop()'s _cts.Cancel() signals
    // the loop. Using the caller's parent token would leave Stop()'s await hanging until the parent
    // cancels — which the connector only does AFTER Stop() returns (shutdown deadlock).
    public void StartReconciliation() =>
        _reconcileTask = RunReconciliationLoop(_cts!.Token);

    // Per-session data the venue connector needs to build a snapshot (it owns the transport-side
    // bars + exchange balance/trades; the dispatcher owns the session table + order ledger).
    public bool TryGetSessionData(Guid sessionId, out SessionSnapshotData data)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            data = new SessionSnapshotData(
                entry.Subscriptions, entry.ExecutionAsset, entry.QuoteAsset, entry.OrderContext);
            return true;
        }

        data = default;
        return false;
    }

    // Session lookup → execution asset, so the venue connector can stamp the neutral report.
    public bool TryResolveAsset(long orderId, out Asset asset)
    {
        if (_router.TryResolveSession(orderId, out var sessionId)
            && _sessions.TryGetValue(sessionId, out var entry))
        {
            asset = entry.ExecutionAsset;
            return true;
        }

        asset = null!;
        return false;
    }

    // Single-reader drain of both per-session channels. Exec (fills/orders, FullMode.Wait) is
    // drained to empty FIRST every iteration so a market-data flood can never starve or delay a
    // fill; market data (DropNewest) is best-effort. Callbacks run serialized on this one task.
    // Termination: both writers completed and both drained => clean exit; CTS cancel => OCE caught.
    internal static async Task DrainSessionQueues(
        ChannelReader<Action> exec,
        ChannelReader<Action> data,
        ILogger logger,
        Guid sessionId,
        CancellationToken ct)
    {
        try
        {
            while (true)
            {
                while (exec.TryRead(out var execAction))
                    RunCallback(execAction, logger, sessionId);

                if (data.TryRead(out var dataAction))
                {
                    RunCallback(dataAction, logger, sessionId);
                    continue;
                }

                var execWait = exec.WaitToReadAsync(ct).AsTask();
                var dataWait = data.WaitToReadAsync(ct).AsTask();
                await Task.WhenAny(execWait, dataWait).ConfigureAwait(false);

                // WaitToReadAsync returns false only when its writer is completed and drained.
                // Both false => nothing left and nothing more coming => exit. Either true => loop
                // re-checks both readers (exec first). Exceptions (e.g. OCE) propagate to the catch.
                var execMore = execWait.IsCompletedSuccessfully && execWait.Result;
                var dataMore = dataWait.IsCompletedSuccessfully && dataWait.Result;
                if (!execMore && !dataMore)
                {
                    // Surface a faulted/cancelled wait if one of them did not complete successfully.
                    if (!execWait.IsCompletedSuccessfully) await execWait.ConfigureAwait(false);
                    if (!dataWait.IsCompletedSuccessfully) await dataWait.ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static void RunCallback(Action action, ILogger logger, Guid sessionId)
    {
        try { action(); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in session {SessionId} event callback", sessionId);
        }
    }

    public async Task AddSession(LiveSessionConfig config, string quoteAsset, CancellationToken ct = default)
    {
        var asset = config.ExecutionAsset;

        // Resolve (or attach to) the account target. The execution asset is threaded through so
        // the factory seeds the account with no shared mutable state. Funds are discovered by the
        // factory. RegisterSymbol accumulates this session's symbol for cancel-on-dispose.
        var target = (AccountTarget)await _router.ResolveTarget(config.AccountName, asset, ct);

        // Co-tenant fence: one account shares one Portfolio, so a session may attach only if its
        // money semantics match the account's seed — same price SCALE and same quote CURRENCY. Both
        // are checked against the target's IMMUTABLE seed (set under the router gate at creation), so
        // concurrent starts can't slip a mismatch past the fence. Reject (releasing the refcount we
        // just took) until a units-bearing Money model lands on Domain.Portfolio.
        var conflict = CoTenancyRule.Conflict(target, asset, quoteAsset);
        if (conflict is not null)
        {
            await _router.ReleaseTarget(config.AccountName, ct);
            throw new ArgumentException(conflict);
        }

        target.RegisterSymbol(asset.Name);
        var accountContext = target.OrderContext;

        // Order→session routing now lives in the router. The account context fires OrderMapped
        // once an exchange order id is assigned; we record it and replay any buffered reports.
        Action<long, Guid> orderMappedHandler = (exchangeId, sId) =>
        {
            _router.TrackOrder(exchangeId, sId);
            DrainBufferedReports(exchangeId);

            // An in-flight order for an already-removed session just got placed — cancel it so it
            // doesn't rest unmanaged. Fires via any still-subscribed co-tenant handler (the event
            // carries the ORIGINATING session); the last-session case is covered by dispose cancel-all.
            if (_removedSessions.ContainsKey(sId))
                accountContext.Cancel(exchangeId);
        };
        accountContext.OrderMapped += orderMappedHandler;

        if (config.Strategy is IEventBusReceiver receiver)
            receiver.SetEventBus(NullEventBus.Instance);

        if (config.Strategy is IOrderContextReceiver orderReceiver)
            orderReceiver.SetOrderContext(target.OrderContextFor(config.SessionId));

        config.Strategy.OnInit();

        var entry = new LiveSessionEntry(
            config.SessionId,
            config.Strategy,
            target,
            config.AccountName,
            config.Subscriptions,
            asset,
            quoteAsset,
            _options.EventQueueCapacity,
            _options.MarketDataQueueCapacity,
            _logger)
        {
            OrderMappedHandler = orderMappedHandler
        };

        _sessions.TryAdd(config.SessionId, entry);

        // Single reader drains exec (fills/orders) with priority over market data.
        entry.ProcessingTask = Task.Run(() => DrainSessionQueues(
            entry.EventQueue.Reader, entry.MarketDataQueue.Reader,
            _logger, entry.SessionId, _cts!.Token));

        // Register-before-first-tick: dispatch must know this session before EnsureSources wires
        // its bar sources, so the very first emitted bar/tick already fans out to it.
        var registration = new LiveSessionRegistration(
            config.SessionId,
            config.Strategy,
            entry.Subscriptions.ToList(),
            entry.MarketDataQueue.Writer);
        _source.Register(registration);

        // Per-instrument scaling: each subscription scales off its own resolved Asset.
        var instrumentScales = InstrumentScaleMap.Build(entry.Subscriptions.ToList());
        await _source.EnsureSources(registration, instrument => instrumentScales[instrument]);

        _logger.LogInformation(
            "Session {SessionId} added to account '{Account}' for {Asset} with {SubCount} subscription(s)",
            config.SessionId, config.AccountName, asset.Name, config.Subscriptions.Count);
    }

    public async Task RemoveSession(Guid sessionId, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
            return;

        // Mark removed BEFORE unsubscribing this session's OrderMapped handler, so an in-flight order
        // this session submitted (still draining the shared order context) is cancelled the moment it
        // re-keys — caught by a co-tenant's still-subscribed handler. (Last-session: dispose cancel-all.)
        _removedSessions.TryAdd(sessionId, 0);

        // Unregister-before-drain: stop new market-data actions from being enqueued before we
        // complete the writers and drain, so nothing races into a queue we are tearing down.
        _dispatch.Unregister(sessionId);
        await _source.RemoveSources(sessionId);

        if (entry.OrderMappedHandler is not null)
            entry.OrderContext.OrderMapped -= entry.OrderMappedHandler;

        // Cancel this session's resting orders before releasing the target — a co-tenant account
        // stays alive, so the target's dispose-time cancel won't fire for this session. Filter by the
        // router's order->session map so we don't touch a co-tenant session's orders.
        var ctx = entry.OrderContext;
        foreach (var order in ctx.GetPendingOrders())
            if (_router.TryResolveSession(order.Id, out var owner) && owner == sessionId)
                ctx.Cancel(order.Id);

        // Drain both queues before releasing the account target.
        entry.EventQueue.Writer.TryComplete();
        entry.MarketDataQueue.Writer.TryComplete();
        if (entry.ProcessingTask is not null)
        {
            try { await entry.ProcessingTask; }
            catch (OperationCanceledException) { }
        }

        // Release the account target. On the last release the router disposes it, which flushes
        // queued orders/cancels and cancels-all open orders on the exchange.
        await _router.ReleaseTarget(entry.AccountName, ct);

        _logger.LogInformation("Session {SessionId} removed from account '{Account}'", sessionId, entry.AccountName);
    }

    public async Task Stop(CancellationToken ct = default)
    {
        // 1. Drain sessions: complete queues and await processing tasks BEFORE cancelling
        //    CTS so queued callbacks (fills, protectives) are not dropped.
        foreach (var entry in _sessions.Values)
        {
            // Unregister-before-drain: stop the data plane from enqueuing new market data.
            _dispatch.Unregister(entry.SessionId);
            await _source.RemoveSources(entry.SessionId);

            if (entry.OrderMappedHandler is not null)
                entry.OrderContext.OrderMapped -= entry.OrderMappedHandler;

            entry.EventQueue.Writer.TryComplete();
            entry.MarketDataQueue.Writer.TryComplete();
            if (entry.ProcessingTask is not null)
            {
                try { await entry.ProcessingTask; }
                catch (OperationCanceledException) { }
            }
        }

        // 2. Dispose the router — disposes every account target: flushes queued
        //    orders/cancels then cancels-all open orders on the exchange.
        await _router.DisposeAsync();

        // 3. Now cancel CTS — stops reconciliation loop
        _cts?.Cancel();

        // 4. Await reconciliation task (already signalled by CTS)
        if (_reconcileTask is not null)
        {
            try { await _reconcileTask; }
            catch (OperationCanceledException) { }
        }

        // 6. Cleanup. (Step 5 — the venue safety-net cancel-all + transport teardown — stays in the
        //    connector, which calls Stop() then tears down its own transport.)
        _sessions.Clear();
    }

    internal static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException oce
        && stoppingToken.IsCancellationRequested
        && oce.CancellationToken == stoppingToken;

    private async Task RunReconciliationLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.ReconciliationInterval);
        // Per-target failure counter so a healthy target can't reset (mask) a persistently-failing one.
        var consecutiveFailures = new Dictionary<AccountTarget, int>();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Reconcile per TARGET, not per session: the broker open-order pushback is account-wide,
                // so the "expected" set must be the UNION of every co-tenant session's TradeRegistry. Diffing
                // it against one session's registry would orphan-cancel a co-tenant's live protective order (#8).
                foreach (var group in _sessions.Values.GroupBy(e => e.Target))
                {
                    var target = group.Key;
                    try
                    {
                        await ReconcileTarget(target, group.ToList(), ct);
                        consecutiveFailures.Remove(target);
                    }
                    catch (Exception ex) when (!IsTrueShutdown(ex, ct))
                    {
                        var count = consecutiveFailures.GetValueOrDefault(target) + 1;
                        consecutiveFailures[target] = count;
                        LogReconciliationFailure(ex, target.AccountName, count);
                    }
                }

                // Drop counters for targets that no longer back any session.
                if (consecutiveFailures.Count > 0)
                {
                    var live = _sessions.Values.Select(e => e.Target).ToHashSet();
                    foreach (var t in consecutiveFailures.Keys.Where(t => !live.Contains(t)).ToList())
                        consecutiveFailures.Remove(t);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // Periodic per-target reconcile: query the account's exchange open orders (via the reconciler's client)
    // and diff against the UNION of every co-tenant session's expected orders. Binance degenerate case is one
    // session per target, so the union equals that single session's registry and behavior is unchanged.
    private async Task ReconcileTarget(AccountTarget target, IReadOnlyList<LiveSessionEntry> sessions, CancellationToken ct)
    {
        var accountContext = target.OrderContext;
        var union = await SnapshotExpectedUnion(sessions, ct);
        if (union.Count == 0)
            return;

        // The diff queries the exchange (reconciler's order client) and compares against the union.
        var expected = union.SelectMany(s => s.Expected).ToList();
        var pendingIds = accountContext.GetPendingOrders()
            .Select(o => o.Id).Where(id => id > 0).ToHashSet();
        var result = await _reconciler.DetectAsync(
            target.SeedAsset.Name, expected, accountContext.ResolveExchangeOrderId, pendingIds, ct);

        await RepairMissingPerSession(union, result.MissingByGroup, ct);

        if (result.OrphanIds.Count > 0)
            await _reconciler.CancelOrphansAsync(target.SeedAsset.Name, result.OrphanIds, ct);
    }

    // IB reconnect path: the broker pushes the account-wide open orders (their exchange ids). Diff that
    // pushback against the UNION of every co-tenant session's expected exchange ids — orphans are the broker
    // ids absent from the union; missing are union orders absent from the pushback (repaired per owning session).
    // The open-order source IS the snapshot (no exchange query), so this does NOT route through DetectAsync;
    // it reuses CancelOrphansAsync + the EventQueue-serialized RepairGroup pattern.
    public async Task ReconcileFromSnapshot(string account, IReadOnlyList<long> brokerOpenOrderIds, CancellationToken ct)
    {
        var sessions = _sessions.Values
            .Where(e => string.Equals(e.AccountName, account, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sessions.Count == 0)
            return;

        var target = sessions[0].Target;
        var accountContext = target.OrderContext;
        var union = await SnapshotExpectedUnion(sessions, ct);

        // Translate every expected order to its exchange id (only those that reached the exchange).
        var expectedExchangeIds = union
            .SelectMany(s => s.Expected)
            .Select(e => accountContext.ResolveExchangeOrderId(e.OrderId))
            .Where(id => id > 0)
            .ToHashSet();

        var brokerSet = new HashSet<long>(brokerOpenOrderIds);

        // Orphans: resting on the broker but expected by NO co-tenant session. Cancel through the TARGET's order
        // client (the connector-level reconciler client is NullExchangeOrderClient for IB). Symbol is the
        // single-asset target's seed; IbExchangeOrderClient.CancelOrderAsync ignores it (IB cancels by id).
        var orphanIds = brokerOpenOrderIds.Where(id => !expectedExchangeIds.Contains(id)).Distinct().ToList();
        if (orphanIds.Count > 0)
            await _reconciler.CancelOrphansAsync(target.OrderClient, target.SeedAsset.Name, orphanIds, ct);

        // Missing: expected by a session but absent from the broker pushback (cancelled/filled during the gap).
        var missingByGroup = new Dictionary<long, HashSet<long>>();
        foreach (var s in union)
            foreach (var exp in s.Expected)
            {
                var exchangeId = accountContext.ResolveExchangeOrderId(exp.OrderId);
                if (exchangeId > 0 && !brokerSet.Contains(exchangeId))
                {
                    if (!missingByGroup.TryGetValue(exp.GroupId, out var set))
                        missingByGroup[exp.GroupId] = set = [];
                    set.Add(exp.OrderId);
                }
            }

        await RepairMissingPerSession(union, missingByGroup, ct);
    }

    private readonly record struct SessionExpected(
        LiveSessionEntry Entry, ITradeRegistryProvider Provider, IReadOnlyList<ExpectedOrder> Expected);

    // Snapshot each session's GetExpectedOrders() on ITS OWN EventQueue (serialized with that session's module
    // mutations) and return them tagged by owning session, so a later RepairGroup runs on the right queue.
    private async Task<IReadOnlyList<SessionExpected>> SnapshotExpectedUnion(
        IReadOnlyList<LiveSessionEntry> sessions, CancellationToken ct)
    {
        var union = new List<SessionExpected>(sessions.Count);
        foreach (var entry in sessions)
        {
            if (entry.Strategy is not ITradeRegistryProvider provider)
                continue;

            // WriteAsync (not TryWrite): on a bounded queue a full buffer would make TryWrite drop the action,
            // leaving `await tcs.Task` hung forever. The single-reader ProcessingTask drains independently.
            var tcs = new TaskCompletionSource<IReadOnlyList<ExpectedOrder>>();
            await entry.EventQueue.Writer.WriteAsync(() =>
                tcs.SetResult(provider.TradeRegistry.GetExpectedOrders()), ct);
            union.Add(new SessionExpected(entry, provider, await tcs.Task));
        }
        return union;
    }

    // Repair each owning session's missing orders on ITS OWN EventQueue so module mutation stays serialized.
    private async Task RepairMissingPerSession(
        IReadOnlyList<SessionExpected> union, Dictionary<long, HashSet<long>> missingByGroup, CancellationToken ct)
    {
        if (missingByGroup.Count == 0)
            return;

        foreach (var s in union)
        {
            // A group id belongs to exactly one session's registry; repair only the groups this session owns.
            var owned = s.Expected.Select(e => e.GroupId).ToHashSet();
            var mine = missingByGroup.Where(kv => owned.Contains(kv.Key)).ToList();
            if (mine.Count == 0)
                continue;

            var repairTcs = new TaskCompletionSource();
            await s.Entry.EventQueue.Writer.WriteAsync(() =>
            {
                foreach (var (groupId, missingIds) in mine)
                    s.Provider.TradeRegistry.RepairGroup(groupId, missingIds);
                repairTcs.SetResult();
            }, ct);
            await repairTcs.Task;
        }
    }

    private void LogReconciliationFailure(Exception ex, string account, int consecutiveFailures)
    {
        if (consecutiveFailures >= 3)
            _logger.LogError(ex,
                "Reconciliation has failed {Count} consecutive times for account {Account}",
                consecutiveFailures, account);
        else
            _logger.LogWarning(ex,
                "Reconciliation failed for account {Account} (attempt {Count})",
                account, consecutiveFailures);
    }

    public void OnExecutionReport(ExecutionReport report)
    {
        // Look up session via the router's order→session map.
        if (!_router.TryResolveSession(report.OrderId, out var sessionId))
        {
            var queue = _bufferedReports.GetOrAdd(report.OrderId, _ => new());
            queue.Enqueue(report);
            _logger.LogDebug("Buffered execution report for unmapped order {OrderId} (type={ExecType})",
                report.OrderId, report.ExecType);
            return;
        }

        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            _logger.LogWarning(
                "Received execution report for order {OrderId} but session {SessionId} no longer exists",
                report.OrderId, sessionId);
            return;
        }

        // Stamp the session's execution asset: the original code always scaled/filled off
        // entry.ExecutionAsset, and a buffered-then-replayed report may carry a placeholder asset the
        // connector seeded before the order mapped. The session is authoritative for the money scale.
        // The connector's placeholder asset is always overwritten here before any scaling.
        report = report with { Asset = entry.ExecutionAsset };

        switch (report.ExecType)
        {
            case ExecType.Trade:
                HandleTrade(report, entry);
                break;

            case ExecType.Canceled:
            case ExecType.Expired:
                HandleTermination(report, entry, OrderStatus.Cancelled);
                break;

            case ExecType.Rejected:
                HandleTermination(report, entry, OrderStatus.Rejected);
                break;
        }
    }

    private void DrainBufferedReports(long orderId)
    {
        if (!_bufferedReports.TryRemove(orderId, out var queue))
            return;

        _logger.LogInformation("Replaying {Count} buffered report(s) for order {OrderId}",
            queue.Count, orderId);

        while (queue.TryDequeue(out var report))
            OnExecutionReport(report);
    }

    private void HandleTrade(ExecutionReport report, LiveSessionEntry entry)
    {
        var accountContext = entry.OrderContext;

        // Skip if fills were already processed from REST response
        if (accountContext.IsOrderRestFilled(report.OrderId))
        {
            _logger.LogDebug(
                "Skipping WebSocket fill for order {OrderId} — already processed from REST",
                report.OrderId);
            return;
        }

        var asset = report.Asset;
        var scale = new ScaleContext(asset);

        var fillPrice = scale.FromMarketPrice(report.LastFillPrice);
        var fillQty = report.LastFillQty;
        var commission = scale.FromMarketPrice(report.Commission);
        var side = report.Side;

        var enqueued = entry.EventQueue.Writer.TryWrite(() =>
        {
            var fill = new Fill(
                report.OrderId,
                asset,
                report.TransactionTime,
                fillPrice,
                fillQty,
                side,
                commission);

            accountContext.AddFill(fill);

            // Update pending order status based on the report's order status
            var pendingOrder = accountContext.GetPendingOrder(report.OrderId);
            if (pendingOrder is not null)
            {
                if (report.Status == OrderStatus.Filled)
                {
                    pendingOrder.Status = OrderStatus.Filled;
                    accountContext.RemovePendingOrder(report.OrderId);
                    _router.UntrackOrder(report.OrderId);
                }
                else if (report.Status == OrderStatus.PartiallyFilled)
                {
                    pendingOrder.Status = OrderStatus.PartiallyFilled;
                }
            }

            // Fills always deliver — every IStrategy implements OnTrade(Fill, Order).
            var order = pendingOrder ?? new Order
            {
                Id = report.OrderId,
                Asset = asset,
                Side = side,
                Type = report.Type,
                Quantity = report.OriginalQuantity,
            };

            entry.Strategy.OnTrade(fill, order);

            _logger.LogInformation(
                "Trade execution: {Side} {Qty} {Symbol} @ {Price} (status={Status}, session={SessionId})",
                report.Side, report.LastFillQty, asset.Name, report.LastFillPrice,
                report.Status, entry.SessionId);
        });

        if (!enqueued)
            _logger.LogError(
                "EventQueue full for session {SessionId} — TRADE execution report for order {OrderId} could not be enqueued",
                entry.SessionId, report.OrderId);
    }

    private void HandleTermination(ExecutionReport report, LiveSessionEntry entry, OrderStatus terminalStatus)
    {
        var accountContext = entry.OrderContext;

        var enqueued = entry.EventQueue.Writer.TryWrite(() =>
        {
            var pendingOrder = accountContext.GetPendingOrder(report.OrderId);
            if (pendingOrder is not null)
            {
                pendingOrder.Status = terminalStatus;
                accountContext.RemovePendingOrder(report.OrderId);
            }

            _router.UntrackOrder(report.OrderId);
        });

        if (!enqueued)
            _logger.LogError(
                "EventQueue full for session {SessionId} — termination report ({ExecType}) for order {OrderId} could not be enqueued",
                entry.SessionId, report.ExecType, report.OrderId);

        _logger.LogInformation(
            "Order {OrderId} terminated: {ExecType} → {Status} (session={SessionId})",
            report.OrderId, report.ExecType, terminalStatus, entry.SessionId);
    }
}
