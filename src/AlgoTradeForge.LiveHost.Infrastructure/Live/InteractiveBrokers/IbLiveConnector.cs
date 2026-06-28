using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// The IB order-plane composition root, cohabiting the shared IB socket with the Plan 3 data plane.
//
// IB inversion vs Binance: Binance is one connector PER account (account == transport). IB is N sub-accounts
// over ONE login/socket, so this is a SINGLE shared connector for the whole login. It routes config.AccountName
// internally via OrderRouter -> AccountTarget. IbLiveAccountManager hands this same instance back for any account.
//
// ConnectAsync seeds the order-id space (session.Connect -> IbConnection seeds NextOrderId), builds the order
// gateway over the shared socket, the neutral AccountTargetFactory with IB providers (C1 order client + C2 funds),
// the data source over the shared dispatch/tick router (C4), the OrderRouter + LiveSessionDispatcher, then starts
// the dispatcher. The session lifecycle + report routing live in the dispatcher; this root owns transport wiring +
// venue-specific quote-currency resolution only.
internal sealed class IbLiveConnector : ILiveConnector
{
    private readonly IIbMarketDataSession _session;
    private readonly IIbContractResolver _contractResolver;
    private readonly IIbAccountSummaryClient _summaryClient;
    private readonly IOrderValidator _orderValidator;
    private readonly ITickRouter _tickRouter;
    private readonly IStrategyDispatch _dispatch;
    private readonly LiveDispatcherOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IbLiveConnector> _logger;

    // Built from IbConnection + IbWrapper in production; the only un-fakeable seam (it owns the real socket),
    // so it is injected as a factory the tests substitute. The connector supplies the onReport sink.
    private readonly Func<Action<ExecutionReport>, IIbOrderGateway> _gatewayFactory;

    private CancellationTokenSource? _cts;
    private IIbOrderGateway? _gateway;
    private IMarketDataSource? _source;
    private IOrderRouter? _router;
    private OrderGroupReconciler? _reconciler;
    private LiveSessionDispatcher? _dispatcher;
    private Task? _reconcileOnReconnect;
    // Serializes concurrent reconnect-reconcile passes so two rapid gateway flaps can't run two
    // ReconcileFromSnapshot sweeps concurrently against the same targets.
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    public string AccountName { get; }
    public LiveSessionStatus Status { get; private set; } = LiveSessionStatus.Idle;
    public int SessionCount => _dispatcher?.Count ?? 0;

    public IbLiveConnector(
        string accountName,
        IIbMarketDataSession session,
        IIbContractResolver contractResolver,
        IIbAccountSummaryClient summaryClient,
        IOrderValidator orderValidator,
        ITickRouter tickRouter,
        IStrategyDispatch dispatch,
        LiveDispatcherOptions options,
        ILoggerFactory loggerFactory,
        Func<Action<ExecutionReport>, IIbOrderGateway> gatewayFactory)
    {
        AccountName = accountName;
        _session = session;
        _contractResolver = contractResolver;
        _summaryClient = summaryClient;
        _orderValidator = orderValidator;
        _tickRouter = tickRouter;
        _dispatch = dispatch;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<IbLiveConnector>();
        _gatewayFactory = gatewayFactory;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Status = LiveSessionStatus.Connecting;
        _cts = new CancellationTokenSource();

        try
        {
            // Establishes the shared socket and seeds the order-id space (IbConnection re-arms NextOrderId
            // from nextValidId). Idempotent if the data plane already connected the same session.
            await _session.Connect(ct);

            // Order plane over the shared socket: the gateway joins inbound fills to per-order context and
            // emits neutral ExecutionReports; we route those into the dispatcher.
            _gateway = _gatewayFactory(report => _dispatcher!.OnExecutionReport(report));

            _source = new DispatchMarketDataSource(_dispatch, _tickRouter);

            // IB providers for the venue-neutral AccountTargetFactory: per-account order client (C1) + funds (C2).
            // A target is single-asset for Plan 4 scope; the execution asset is threaded in per ResolveTarget.
            var factory = new AccountTargetFactory(
                fundsFor: (_, _) => new IbAccountFundsSource(_summaryClient),
                clientFor: (account, asset) => new IbExchangeOrderClient(account, asset, _gateway!, _contractResolver),
                _orderValidator,
                _logger,
                _options.EventQueueCapacity);

            _router = new OrderRouter(factory, _loggerFactory.CreateLogger<OrderRouter>());

            // Reconciler needs a connector-level IExchangeOrderClient, but IB order clients are per-account
            // (per-target). Per-target union reconciliation is E1's job; until then a null client makes the
            // reconcile loop a no-op (no open-order query, nothing to repair/cancel). Flagged for E1.
            _reconciler = new OrderGroupReconciler(NullExchangeOrderClient.Instance, _logger);

            _dispatcher = new LiveSessionDispatcher(
                _router, _source, _dispatch, _reconciler, _options, _logger);
            _dispatcher.Start(_cts.Token);
            // StartReconciliation is intentionally NOT called here. IB order clients are per-account
            // (per-target), so the NullExchangeOrderClient above always returns empty open-orders.
            // Starting the loop with that placeholder would make DetectAsync treat every expected
            // protective order as missing and re-submit duplicates every ~30 s. E1 supplies per-target
            // union reconciliation and calls StartReconciliation at that point.

            // Reconnect trigger: on a socket reconnect, pull the broker's account-wide open-order pushback and
            // reconcile each account against its co-tenant UNION (the #8-safe ReconcileFromSnapshot).
            _session.Reconnected += OnSessionReconnected;

            Status = LiveSessionStatus.Running;
            _logger.LogInformation("IB connector '{Account}' connected (shared socket: data + orders)", AccountName);
        }
        catch (Exception ex)
        {
            Status = LiveSessionStatus.Error;
            _logger.LogError(ex, "Failed to connect IB connector '{Account}'", AccountName);
            throw;
        }
    }

    public async Task AddSessionAsync(LiveSessionConfig config, CancellationToken ct = default)
    {
        if (Status != LiveSessionStatus.Running)
            throw new InvalidOperationException($"IB connector '{AccountName}' is not running.");

        // Venue quote-currency: the execution asset's IB contract carries the currency (USD for equities/futures
        // today). The dispatcher's co-tenancy fence checks it against the account-target seed.
        var quoteAsset = ResolveQuoteCurrency(config.ExecutionAsset);

        await _dispatcher!.AddSession(config, quoteAsset, ct);

        // Contract-currency and funds-currency must agree until the units-bearing Money model lands.
        // A mismatch here means IbAccountFundsSource and the IB contract report different currencies for
        // the same account — fail loud so this surfaces immediately rather than silently mis-fencing.
        var target = (AccountTarget)_router!.Targets.First(t => t.AccountName == config.AccountName);
        if (!string.Equals(target.SeedQuoteAsset, quoteAsset, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"IB contract currency '{quoteAsset}' for asset '{config.ExecutionAsset.Name}' does not match " +
                $"the funds-discovered quote currency '{target.SeedQuoteAsset}' for account '{config.AccountName}'. " +
                "Both sources must agree until a units-bearing Money model is in place.");
    }

    private static string ResolveQuoteCurrency(Asset executionAsset)
    {
        var currency = executionAsset.ToIbContract().Currency;
        return string.IsNullOrEmpty(currency) ? "USD" : currency;
    }

    public async Task RemoveSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (_dispatcher is not null)
            await _dispatcher.RemoveSession(sessionId, ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Status is LiveSessionStatus.Stopped or LiveSessionStatus.Stopping)
            return;

        Status = LiveSessionStatus.Stopping;
        _logger.LogInformation("Stopping IB connector '{Account}'", AccountName);

        try
        {
            _session.Reconnected -= OnSessionReconnected;

            // Dispatcher drains sessions, disposes the router (cancels-all per target), cancels its CTS,
            // awaits reconciliation. The shared IbSession is a DI singleton disposed by the container — the
            // data plane co-tenants it, so this connector does NOT tear the transport down here.
            if (_dispatcher is not null)
                await _dispatcher.Stop(ct);

            _cts?.Cancel();

            // Await any in-flight reconnect reconciliation so its pushback round-trip can't outlive the connector.
            if (_reconcileOnReconnect is not null)
            {
                try { await _reconcileOnReconnect; }
                catch (OperationCanceledException) { }
            }

            if (_gateway is IAsyncDisposable disposableGateway)
                await disposableGateway.DisposeAsync();

            _reconcileGate.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping IB connector '{Account}'", AccountName);
        }
        finally
        {
            Status = LiveSessionStatus.Stopped;
            _logger.LogInformation("IB connector '{Account}' stopped", AccountName);
        }
    }

    // On a socket reconnect the gateway is the transport-focused source (it owns the wrapper + socket): it pulls
    // the broker's account-wide open-order pushback. The dispatcher is the session-focused diff: it reconciles
    // each account against its co-tenant UNION. The Reconnected event is sync, so orchestrate on a tracked task
    // (the pushback round-trip is async) with the connector's CTS so Stop() can await/cancel it.
    private void OnSessionReconnected()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        _reconcileOnReconnect = Task.Run(() => ReconcileOnReconnect(ct), ct);
    }

    private async Task ReconcileOnReconnect(CancellationToken ct)
    {
        if (_gateway is null || _dispatcher is null)
            return;
        try
        {
            using var _ = await _reconcileGate.LockAsync(ct);
            _logger.LogInformation("IB session reconnected for '{Account}'; reconciling open orders against the co-tenant union", AccountName);
            var byAccount = await _gateway.SnapshotOpenOrders(ct);
            foreach (var (account, ids) in byAccount)
                await _dispatcher.ReconcileFromSnapshot(account, ids, ct);
        }
        catch (Exception ex) when (!LiveSessionDispatcher.IsTrueShutdown(ex, ct))
        {
            _logger.LogError(ex, "Reconnect reconciliation failed for IB connector '{Account}'", AccountName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Status is not LiveSessionStatus.Stopped)
            await StopAsync();
    }
}
