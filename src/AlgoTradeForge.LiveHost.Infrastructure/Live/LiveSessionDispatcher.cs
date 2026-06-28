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

    public void StartReconciliation(CancellationToken ct) =>
        _reconcileTask = RunReconciliationLoop(ct);

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
        // Per-session failure counter so a healthy session can't reset (mask) a persistently-failing one.
        var consecutiveFailures = new Dictionary<Guid, int>();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Each session reconciles its own TradeRegistry against the shared account ledger
                // (entry.OrderContext). One account backs all its co-tenant sessions.
                foreach (var entry in _sessions.Values)
                {
                    if (entry.Strategy is not ITradeRegistryProvider provider)
                        continue;
                    try
                    {
                        await ReconcileSession(entry, provider, entry.OrderContext, ct);
                        consecutiveFailures.Remove(entry.SessionId);
                    }
                    catch (Exception ex) when (!IsTrueShutdown(ex, ct))
                    {
                        var count = consecutiveFailures.GetValueOrDefault(entry.SessionId) + 1;
                        consecutiveFailures[entry.SessionId] = count;
                        LogReconciliationFailure(ex, entry.SessionId, count);
                    }
                }

                // Drop counters for sessions that have since been removed.
                if (consecutiveFailures.Count > 0)
                    foreach (var id in consecutiveFailures.Keys.Where(id => !_sessions.ContainsKey(id)).ToList())
                        consecutiveFailures.Remove(id);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReconcileSession(
        LiveSessionEntry entry, ITradeRegistryProvider provider, LiveOrderContext accountContext, CancellationToken ct)
    {
        // Known edge case: if a fill is in-progress (WebSocket report received but not yet
        // processed on the event queue), reconciliation may see the protective order as missing
        // and submit a duplicate. The duplicate will be rejected by the exchange (order already
        // exists) or cleaned up on the next reconciliation cycle as an orphan.

        // Phase 1: Snapshot expected orders on EventQueue (thread-safe read).
        // WriteAsync (not TryWrite): on a bounded queue a full buffer would make
        // TryWrite drop the action, leaving `await tcs.Task` hung forever. The
        // single-reader ProcessingTask drains independently, so WriteAsync always
        // gets a slot and the round-trip completes.
        var tcs = new TaskCompletionSource<IReadOnlyList<ExpectedOrder>>();
        await entry.EventQueue.Writer.WriteAsync(() =>
            tcs.SetResult(provider.TradeRegistry.GetExpectedOrders()), ct);
        var expected = await tcs.Task;

        // Phase 2: Detect on timer thread (exchange query, pure comparison)
        var pendingIds = accountContext.GetPendingOrders()
            .Select(o => o.Id).Where(id => id > 0).ToHashSet();
        var result = await _reconciler.DetectAsync(
            entry.ExecutionAsset.Name, expected,
            accountContext.ResolveExchangeOrderId, pendingIds, ct);

        // Phase 3a: Repair on EventQueue (module mutation serialized)
        if (result.MissingByGroup.Count > 0)
        {
            var repairTcs = new TaskCompletionSource();
            await entry.EventQueue.Writer.WriteAsync(() =>
            {
                foreach (var (groupId, missingIds) in result.MissingByGroup)
                    provider.TradeRegistry.RepairGroup(groupId, missingIds);
                repairTcs.SetResult();
            }, ct);
            await repairTcs.Task;
        }

        // Phase 3b: Cancel orphans directly on exchange (no module state)
        if (result.OrphanIds.Count > 0)
            await _reconciler.CancelOrphansAsync(entry.ExecutionAsset.Name, result.OrphanIds, ct);
    }

    private void LogReconciliationFailure(Exception ex, Guid sessionId, int consecutiveFailures)
    {
        if (consecutiveFailures >= 3)
            _logger.LogError(ex,
                "Reconciliation has failed {Count} consecutive times for session {SessionId}",
                consecutiveFailures, sessionId);
        else
            _logger.LogWarning(ex,
                "Reconciliation failed for session {SessionId} (attempt {Count})",
                sessionId, consecutiveFailures);
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
                DateTimeOffset.UtcNow,
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
                Type = OrderType.Market,
                Quantity = report.LastFillQty,
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
