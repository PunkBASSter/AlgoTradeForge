using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

/// <remarks>
/// State machine invariant: <c>_apiClient</c>, <c>_reconciler</c>, <c>_source</c> and
/// <c>_router</c> are set in <c>ConnectAsync</c> before <c>Status</c> transitions to
/// <c>Running</c>. All methods that use the <c>!</c> forms are only reachable after that
/// transition (guarded by Status checks or by being callbacks from subsystems started during
/// <c>ConnectAsync</c>).
///
/// Composition root: the connector owns the WS/user-data lifecycle, reconciliation timer and a
/// per-session registry, but delegates the order side to <c>IOrderRouter</c> + <c>IAccountTarget</c>
/// and the data side to <c>IMarketDataSource</c>. Order→session resolution lives in the router.
/// </remarks>
public sealed class BinanceLiveConnector : ILiveConnector
{
    private readonly BinanceAccountConfig _accountConfig;
    private readonly BinanceLiveOptions _sharedOptions;
    private readonly IOrderValidator _orderValidator;
    private readonly ITickRouter _tickRouter;
    private readonly IStrategyDispatch _dispatch;
    private readonly ILogger<BinanceLiveConnector> _logger;

    private CancellationTokenSource? _cts;
    private BinanceApiClient? _apiClient;
    private BinanceWebSocketManager? _wsManager;

    // Order + data seams, built internally in ConnectAsync (they depend on _apiClient).
    private IMarketDataSource? _source;
    private IOrderRouter? _router;
    private BinanceAccountFundsSource? _fundsSource;
    private BinanceAccountTargetFactory? _factory;

    // Set by AddSessionAsync before ResolveTarget so the factory's assetForAccount() resolves.
    private Asset? _accountAsset;

    private readonly ConcurrentDictionary<Guid, LiveSessionEntry> _sessions = new();
    private readonly ConcurrentDictionary<long, ConcurrentQueue<BinanceExecutionReport>> _bufferedReports = new();

    private OrderGroupReconciler? _reconciler;
    private Task? _reconcileTask;

    // Cache fields are accessed from multiple threads (snapshot requests + kline callbacks).
    // decimal (128-bit) and DateTimeOffset are not atomically readable on x64, so we
    // protect all reads/writes with _cacheLock to prevent torn reads.
    private readonly Lock _cacheLock = new();
    private decimal _cachedQuoteBalance;
    private DateTimeOffset _balanceCacheExpiry;

    private IReadOnlyList<ExchangeTradeDto> _cachedTrades = [];
    private DateTimeOffset _tradeCacheExpiry;

    public string AccountName { get; }
    public LiveSessionStatus Status { get; private set; } = LiveSessionStatus.Idle;
    public int SessionCount => _sessions.Count;

    private sealed class LiveSessionEntry
    {
        private readonly StrongBox<long> _droppedMarketData = new(0L);

        public Guid SessionId { get; }
        public IInt64BarStrategy Strategy { get; }
        public IAccountTarget Target { get; }
        public string AccountName { get; }
        public IReadOnlyList<DataFeedSubscription> Subscriptions { get; }
        public Asset ExecutionAsset { get; }
        public string QuoteAsset { get; }

        public Channel<Action> EventQueue { get; }

        // Market data is best-effort: drop the newest item under saturation so a flood
        // never back-pressures or starves the exec queue (fills/orders).
        public Channel<Action> MarketDataQueue { get; }

        public long DroppedMarketDataCount => Interlocked.Read(ref _droppedMarketData.Value);

        public Task? ProcessingTask { get; set; }

        // Stored so the lambda can be removed from OrderMapped on session teardown.
        public Action<long, Guid>? OrderMappedHandler { get; set; }

        // The account-scoped order ledger backing this session (shared across sessions on the
        // same account). Reached via Target for fills/pending-order/reconciliation paths.
        public LiveOrderContext OrderContext => ((AccountTarget)Target).OrderContext;

        public LiveSessionEntry(
            Guid sessionId,
            IInt64BarStrategy strategy,
            IAccountTarget target,
            string accountName,
            IReadOnlyList<DataFeedSubscription> subscriptions,
            Asset executionAsset,
            string quoteAsset,
            int eventQueueCapacity,
            int marketDataQueueCapacity,
            ILogger logger)
        {
            SessionId = sessionId;
            Strategy = strategy;
            Target = target;
            AccountName = accountName;
            Subscriptions = subscriptions;
            ExecutionAsset = executionAsset;
            QuoteAsset = quoteAsset;

            EventQueue = Channel.CreateBounded<Action>(
                new BoundedChannelOptions(eventQueueCapacity)
                { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

            var box = _droppedMarketData;
            MarketDataQueue = Channel.CreateBounded<Action>(
                new BoundedChannelOptions(marketDataQueueCapacity)
                { SingleReader = true, FullMode = BoundedChannelFullMode.DropNewest },
                itemDropped: _ =>
                {
                    var n = Interlocked.Increment(ref box.Value);
                    if ((n & 0x3FF) == 0)
                        logger.LogDebug("Session {SessionId} dropped {Count} market-data callbacks (queue saturated)",
                            sessionId, n);
                });
        }
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

    public BinanceLiveConnector(
        string accountName,
        BinanceAccountConfig accountConfig,
        BinanceLiveOptions sharedOptions,
        IOrderValidator orderValidator,
        ITickRouter tickRouter,
        IStrategyDispatch dispatch,
        ILogger<BinanceLiveConnector> logger)
    {
        AccountName = accountName;
        _accountConfig = accountConfig;
        _sharedOptions = sharedOptions;
        _orderValidator = orderValidator;
        _tickRouter = tickRouter;
        _dispatch = dispatch;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Status = LiveSessionStatus.Connecting;
        _cts = new CancellationTokenSource();

        try
        {
            if (string.IsNullOrEmpty(_accountConfig.ApiKey) || string.IsNullOrEmpty(_accountConfig.ApiSecret))
            {
                throw new InvalidOperationException(
                    $"API credentials are not configured for account '{AccountName}'. " +
                    $"Set BinanceLive:Accounts:{AccountName}:ApiKey and BinanceLive:Accounts:{AccountName}:ApiSecret.");
            }

            _apiClient = new BinanceApiClient(
                _accountConfig.RestUrl, _accountConfig.ApiKey, _accountConfig.ApiSecret, _logger);

            // Sync local clock with Binance server to avoid timestamp rejection
            await _apiClient.SyncTimeAsync(ct);

            // Build the order + data seams now that _apiClient exists. The factory discovers
            // funds + symbols-to-cancel lazily per account at ResolveTarget time.
            _source = new BinanceMarketDataSource(_dispatch, _tickRouter);
            _fundsSource = new BinanceAccountFundsSource(_apiClient);
            _factory = new BinanceAccountTargetFactory(
                _fundsSource, _apiClient, _orderValidator, _logger,
                _sharedOptions.LiveChannelCapacity,
                assetForAccount: () => _accountAsset!,
                symbolsForAccount: () => _sessions.Values
                    .Select(e => e.ExecutionAsset.Name)
                    .Distinct()
                    .ToList());
            _router = new OrderRouter(_factory, NullLogger<OrderRouter>.Instance);

            _wsManager = new BinanceWebSocketManager(
                _accountConfig.MarketStreamUrl,
                _sharedOptions.ReconnectDelay, _sharedOptions.MaxReconnectAttempts,
                _logger);
            _wsManager.Start(_cts);

            // Subscribe to user data via WebSocket API — awaited so we know it's active
            await _wsManager.ConnectUserDataWsApi(
                _accountConfig.WebSocketApiUrl, _accountConfig.ApiKey,
                _apiClient.Sign, _apiClient.GetTimestamp, OnExecutionReport);

            _reconciler = new OrderGroupReconciler(_apiClient, _logger);
            _reconcileTask = RunReconciliationLoop(_cts.Token);

            Status = LiveSessionStatus.Running;
            _logger.LogInformation(
                "Connector for account '{Account}' connected. REST={RestUrl}",
                AccountName, _accountConfig.RestUrl);
        }
        catch (Exception ex)
        {
            Status = LiveSessionStatus.Error;
            _logger.LogError(ex, "Failed to connect account '{Account}'", AccountName);
            throw;
        }
    }

    internal async Task<decimal> GetTickerPriceAsync(string symbol, CancellationToken ct = default)
    {
        if (_apiClient is null)
            throw new InvalidOperationException("Connector is not connected.");
        return await _apiClient.GetTickerPriceAsync(symbol, ct);
    }

    public async Task AddSessionAsync(LiveSessionConfig config, CancellationToken ct = default)
    {
        if (Status != LiveSessionStatus.Running)
            throw new InvalidOperationException($"Connector for account '{AccountName}' is not running.");

        var asset = config.ExecutionAsset;

        // Quote asset is still needed by GetSessionSnapshotAsync for exchange-balance display.
        var symbolInfo = await _apiClient!.GetExchangeInfoAsync(asset.Name, ct);

        // Resolve (or attach to) the account target. The factory reads _accountAsset, so it MUST
        // be set before ResolveTarget. Funds are discovered by the factory.
        _accountAsset = asset;
        var target = await _router!.ResolveTarget(config.AccountName, ct);
        var accountContext = ((AccountTarget)target).OrderContext;

        // Order→session routing now lives in the router. The account context fires OrderMapped
        // once an exchange order id is assigned; we record it and replay any buffered reports.
        Action<long, Guid> orderMappedHandler = (exchangeId, sId) =>
        {
            _router.TrackOrder(exchangeId, sId);
            DrainBufferedReports(exchangeId);
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
            symbolInfo.QuoteAsset,
            _sharedOptions.LiveChannelCapacity,
            _sharedOptions.MarketDataChannelCapacity,
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
        _source!.Register(registration);

        // Per-instrument scaling: each subscription scales off its own resolved Asset.
        var instrumentScales = InstrumentScaleMap.Build(entry.Subscriptions.ToList());
        await _source.EnsureSources(registration, instrument => instrumentScales[instrument]);

        _logger.LogInformation(
            "Session {SessionId} added to account '{Account}' for {Asset} with {SubCount} subscription(s)",
            config.SessionId, AccountName, asset.Name, config.Subscriptions.Count);
    }

    public async Task RemoveSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
            return;

        // Unregister-before-drain: stop new market-data actions from being enqueued before we
        // complete the writers and drain, so nothing races into a queue we are tearing down.
        _dispatch.Unregister(sessionId);
        await _source!.RemoveSources(sessionId);

        if (entry.OrderMappedHandler is not null)
            entry.OrderContext.OrderMapped -= entry.OrderMappedHandler;

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
        await _router!.ReleaseTarget(entry.AccountName, ct);

        _logger.LogInformation("Session {SessionId} removed from account '{Account}'", sessionId, AccountName);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Status is LiveSessionStatus.Stopped or LiveSessionStatus.Stopping)
            return;

        Status = LiveSessionStatus.Stopping;
        _logger.LogInformation("Stopping connector for account '{Account}'", AccountName);

        try
        {
            // 1. Drain sessions: complete queues and await processing tasks BEFORE cancelling
            //    CTS so queued callbacks (fills, protectives) are not dropped.
            foreach (var entry in _sessions.Values)
            {
                // Unregister-before-drain: stop the data plane from enqueuing new market data.
                _dispatch.Unregister(entry.SessionId);
                if (_source is not null)
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
            if (_router is not null)
                await _router.DisposeAsync();

            // 3. Now cancel CTS — stops WebSocket/kline/reconciliation loops
            _cts?.Cancel();

            // 4. Await reconciliation task (already signalled by CTS)
            if (_reconcileTask is not null)
            {
                try { await _reconcileTask; }
                catch (OperationCanceledException) { }
            }

            // 5. Safety-net: cancel all open orders on exchange per symbol (belt-and-suspenders,
            //    covers multi-symbol accounts even though the target dispose already cancelled).
            if (_apiClient is not null)
            {
                foreach (var entry in _sessions.Values)
                {
                    try
                    {
                        await _apiClient.CancelAllOpenOrdersAsync(entry.ExecutionAsset.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Safety-net cancel-all failed for {Symbol}", entry.ExecutionAsset.Name);
                    }
                }
            }

            // 6. Cleanup
            _sessions.Clear();

            if (_wsManager is not null)
                await _wsManager.DisposeAsync();

            _apiClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping connector for account '{Account}'", AccountName);
        }
        finally
        {
            Status = LiveSessionStatus.Stopped;
            _logger.LogInformation("Connector for account '{Account}' stopped", AccountName);
        }
    }

    internal static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException oce
        && stoppingToken.IsCancellationRequested
        && oce.CancellationToken == stoppingToken;

    private async Task RunReconciliationLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_sharedOptions.ReconciliationInterval);
        var consecutiveFailures = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Reconcile per account target. A target may back multiple sessions; each
                // session with a TradeRegistry contributes its own expected-orders snapshot.
                foreach (var target in _router!.Targets)
                {
                    var accountContext = ((AccountTarget)target).OrderContext;
                    foreach (var entry in _sessions.Values)
                    {
                        if (!string.Equals(entry.AccountName, target.AccountName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (entry.Strategy is not ITradeRegistryProvider provider)
                            continue;
                        try
                        {
                            await ReconcileSession(entry, provider, accountContext, ct);
                            consecutiveFailures = 0;
                        }
                        catch (Exception ex) when (!IsTrueShutdown(ex, ct))
                        {
                            consecutiveFailures++;
                            LogReconciliationFailure(ex, entry.SessionId, consecutiveFailures);
                        }
                    }
                }
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
        var result = await _reconciler!.DetectAsync(
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

    private void OnExecutionReport(BinanceExecutionReport report)
    {
        // Look up session via the router's order→session map.
        if (!_router!.TryResolveSession(report.OrderId, out var sessionId))
        {
            var queue = _bufferedReports.GetOrAdd(report.OrderId, _ => new());
            queue.Enqueue(report);
            _logger.LogDebug("Buffered execution report for unmapped order {OrderId} (type={ExecType})",
                report.OrderId, report.ExecutionType);
            return;
        }

        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            _logger.LogWarning(
                "Received execution report for order {OrderId} but session {SessionId} no longer exists",
                report.OrderId, sessionId);
            return;
        }

        switch (report.ExecutionType)
        {
            case "TRADE":
                HandleTradeExecution(report, entry);
                break;

            case "CANCELED":
            case "EXPIRED":
                HandleOrderTermination(report, entry, OrderStatus.Cancelled);
                break;

            case "REJECTED":
                HandleOrderTermination(report, entry, OrderStatus.Rejected);
                break;
        }
    }

    private void DrainBufferedReports(long binanceOrderId)
    {
        if (!_bufferedReports.TryRemove(binanceOrderId, out var queue))
            return;

        _logger.LogInformation("Replaying {Count} buffered report(s) for order {OrderId}",
            queue.Count, binanceOrderId);

        while (queue.TryDequeue(out var report))
            OnExecutionReport(report);
    }

    private void HandleTradeExecution(BinanceExecutionReport report, LiveSessionEntry entry)
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

        var asset = entry.ExecutionAsset;
        var scale = new ScaleContext(asset);

        // Parse outside the callback for efficiency
        var fillPrice = scale.FromMarketPrice(decimal.Parse(report.LastFilledPrice, CultureInfo.InvariantCulture));
        var fillQty = decimal.Parse(report.LastFilledQty, CultureInfo.InvariantCulture);
        var commission = scale.FromMarketPrice(decimal.Parse(report.Commission, CultureInfo.InvariantCulture));
        var side = report.Side == "BUY" ? OrderSide.Buy : OrderSide.Sell;

        var enqueued = entry.EventQueue.Writer.TryWrite(() =>
        {
            var fill = new Fill(
                report.OrderId,
                asset,
                DateTimeOffset.FromUnixTimeMilliseconds(report.TransactionTime),
                fillPrice,
                fillQty,
                side,
                commission);

            accountContext.AddFill(fill);

            // Update pending order status based on Binance order status
            var pendingOrder = accountContext.GetPendingOrder(report.OrderId);
            if (pendingOrder is not null)
            {
                if (report.OrderStatus == "FILLED")
                {
                    pendingOrder.Status = OrderStatus.Filled;
                    accountContext.RemovePendingOrder(report.OrderId);
                    _router!.UntrackOrder(report.OrderId);
                }
                else if (report.OrderStatus == "PARTIALLY_FILLED")
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
                Type = ParseBinanceOrderType(report.OrderType),
                Quantity = decimal.Parse(report.OriginalQuantity, CultureInfo.InvariantCulture),
            };

            entry.Strategy.OnTrade(fill, order);

            _logger.LogInformation(
                "Trade execution: {Side} {Qty} {Symbol} @ {Price} (status={Status}, session={SessionId})",
                report.Side, report.LastFilledQty, report.Symbol, report.LastFilledPrice,
                report.OrderStatus, entry.SessionId);
        });

        if (!enqueued)
            _logger.LogError(
                "EventQueue full for session {SessionId} — TRADE execution report for order {OrderId} could not be enqueued",
                entry.SessionId, report.OrderId);
    }

    private void HandleOrderTermination(BinanceExecutionReport report, LiveSessionEntry entry, OrderStatus terminalStatus)
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

            _router!.UntrackOrder(report.OrderId);
        });

        if (!enqueued)
            _logger.LogError(
                "EventQueue full for session {SessionId} — termination report ({ExecType}) for order {OrderId} could not be enqueued",
                entry.SessionId, report.ExecutionType, report.OrderId);

        _logger.LogInformation(
            "Order {OrderId} terminated: {ExecType} → {Status} (session={SessionId})",
            report.OrderId, report.ExecutionType, terminalStatus, entry.SessionId);
    }

    internal async Task<LiveSessionSnapshot?> GetSessionSnapshotAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            return null;

        // Bars + last-bar-per-subscription come from the data-plane bar sources' Recent rings.
        var barFields = SessionSnapshotBars.Build(
            entry.Subscriptions.ToList(),
            _source!.RecentBars);

        var exchangeBalance = await GetCachedQuoteBalanceAsync(entry.QuoteAsset, ct);
        var exchangeTrades = await GetCachedTradesAsync(entry.ExecutionAsset.Name, ct);

        var ctx = entry.OrderContext;
        return new LiveSessionSnapshot(
            barFields.Bars,
            ctx.GetAllFills(),
            ctx.GetPendingOrders(),
            ctx.GetPositions(),
            ctx.Cash,
            ctx.Portfolio.InitialCash,
            exchangeBalance,
            entry.ExecutionAsset,
            entry.Subscriptions.ToList(),
            barFields.LastBarsPerSubscription,
            exchangeTrades);
    }

    private async Task<decimal> GetCachedQuoteBalanceAsync(string quoteAsset, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (DateTimeOffset.UtcNow < _balanceCacheExpiry)
                return _cachedQuoteBalance;
        }

        try
        {
            var accountInfo = await _apiClient!.GetAccountInfoAsync(ct);
            var balance = accountInfo.Balances
                .FirstOrDefault(b => b.Asset.Equals(quoteAsset, StringComparison.OrdinalIgnoreCase));

            lock (_cacheLock)
            {
                _cachedQuoteBalance = balance is not null
                    ? decimal.Parse(balance.Free, CultureInfo.InvariantCulture)
                    : 0m;
                _balanceCacheExpiry = DateTimeOffset.UtcNow.AddSeconds(15);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh exchange balance for {QuoteAsset}", quoteAsset);
        }

        lock (_cacheLock)
            return _cachedQuoteBalance;
    }

    private async Task<IReadOnlyList<ExchangeTradeDto>> GetCachedTradesAsync(string symbol, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (DateTimeOffset.UtcNow < _tradeCacheExpiry)
                return _cachedTrades;
        }

        try
        {
            var trades = await _apiClient!.GetMyTradesAsync(symbol, 50, ct);
            var result = trades
                .Select(t => new ExchangeTradeDto(
                    t.OrderId,
                    DateTimeOffset.FromUnixTimeMilliseconds(t.Time).ToString("O"),
                    decimal.Parse(t.Price, CultureInfo.InvariantCulture),
                    decimal.Parse(t.Qty, CultureInfo.InvariantCulture),
                    t.IsBuyer ? "Buy" : "Sell",
                    decimal.Parse(t.Commission, CultureInfo.InvariantCulture),
                    t.CommissionAsset))
                .ToList();

            lock (_cacheLock)
            {
                _cachedTrades = result;
                _tradeCacheExpiry = DateTimeOffset.UtcNow.AddSeconds(15);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch exchange trades for {Symbol}", symbol);
        }

        lock (_cacheLock)
            return _cachedTrades;
    }

    private static OrderType ParseBinanceOrderType(string type) => type switch
    {
        "MARKET" => OrderType.Market,
        "LIMIT" => OrderType.Limit,
        "STOP_LOSS" => OrderType.Stop,
        "STOP_LOSS_LIMIT" => OrderType.StopLimit,
        _ => OrderType.Market,
    };

    public async ValueTask DisposeAsync()
    {
        if (Status is not LiveSessionStatus.Stopped)
            await StopAsync();
    }
}
