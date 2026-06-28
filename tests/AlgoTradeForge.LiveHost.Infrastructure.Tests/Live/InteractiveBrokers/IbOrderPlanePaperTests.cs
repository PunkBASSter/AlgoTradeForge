using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

// Gated IB paper integration tests for the order plane.
//
// SKIP in CI (IB_PAPER_HOST not set). Run against a real gnzsnz ib-gateway paper stack when
// IB_PAPER_HOST / IB_PAPER_PORT (default 4004) / IB_PAPER_CLIENT_ID (default 11) are set.
// Set IB_PAPER_ACCOUNT to the paper account id (e.g. "DU123456") if known; otherwise the
// harness derives it from reqAccountSummary.
//
// Wiring: real IbWrapper → IbConnection → IbSession + IbConnectionAccountSummaryClient +
// IbConnectionOrderClient → IbOrderGateway → IbLiveConnector → LiveSessionDispatcher.
//
// Teardown: StopAsync → AccountTarget.DisposeAsync cancel-alls open orders so re-runs are clean.
[Trait("Category", "IbPaper")]
public sealed class IbOrderPlanePaperTests
{
    // AAPL equity on SMART/NASDAQ is always resolvable on a paper account.
    private static readonly EquityAsset Aapl = new() { Name = "AAPL", Exchange = "NASDAQ" };
    private static readonly ScaleContext AaplScale = new(Aapl);

    // Penny-wide LMT far from market: placed well below the ask so it always rests, never fills.
    private const decimal LimitFarBelowMarket = 1.00m; // $1 — guaranteed below any real AAPL price

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -----------------------------------------------------------------------
    // 1. MarketOrder_Fills
    // -----------------------------------------------------------------------
    // Place MKT BUY 1 AAPL share. Assert strategy's OnTrade fires and Portfolio reflects a
    // long position. IB paper accounts simulate fills at last price even off-hours. Bounded 30 s.
    [Fact]
    public async Task MarketOrder_Fills()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        await using var h = await Harness.ConnectAsync(Ct);

        var fillTcs = new TaskCompletionSource<Fill>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Strategy.TradeCallback = (fill, _) => fillTcs.TrySetResult(fill);

        h.Strategy.Orders!.Submit(new Order
        {
            Id = 0,
            Asset = Aapl,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 1m,
        });

        var fill = await fillTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(OrderSide.Buy, fill.Side);
        Assert.True(fill.Price > 0, "fill price must be positive");

        // Portfolio position must be long after the fill.
        var positions = h.Strategy.Orders.GetPositions();
        Assert.True(
            positions.TryGetValue("AAPL", out var pos) && pos.Quantity > 0,
            "Portfolio must carry a positive AAPL position after a filled MKT BUY");
    }

    // -----------------------------------------------------------------------
    // 2. LimitOrder_SubmittedThenCancelled
    // -----------------------------------------------------------------------
    // Place LMT BUY far below market; confirm it is resting in the pending set; cancel it;
    // confirm it disappears from pending (dispatcher removes it via HandleTermination when the
    // broker acks the cancellation). Bounded 20 s per step.
    [Fact]
    public async Task LimitOrder_SubmittedThenCancelled()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        await using var h = await Harness.ConnectAsync(Ct);

        var limitTicks = AaplScale.FromMarketPrice(LimitFarBelowMarket);

        // Submit returns the local order id synchronously; the async channel drain sends it to the
        // broker and re-keys the pending entry to the exchange order id. Poll until re-keyed.
        var localId = h.Strategy.Orders!.Submit(new Order
        {
            Id = 0,
            Asset = Aapl,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = 1m,
            LimitPrice = limitTicks,
        });

        Assert.True(localId > 0, "Submit must return a positive local order id");

        // Poll until the order appears as resting (submitted to the broker and acked).
        // The pending set is keyed by exchange id after re-keying; also poll by local id
        // to cover the brief window before re-key completes.
        using var submitCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        submitCts.CancelAfter(TimeSpan.FromSeconds(20));
        await PollUntil(
            () => h.Strategy.Orders.GetPendingOrders().Count > 0,
            submitCts.Token);

        var resting = h.Strategy.Orders.GetPendingOrders();
        Assert.True(resting.Count > 0, "Order must appear in pending set after submission");

        // Grab the (possibly re-keyed) exchange id to cancel by.
        var restingOrder = resting[0];
        var exchangeId = restingOrder.Id;

        // Cancel by local id — Cancel resolves local→exchange via _localToExchangeId.
        h.Strategy.Orders.Cancel(localId);

        // Poll until the order disappears from pending (cancel ack from broker processed).
        using var cancelCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelCts.CancelAfter(TimeSpan.FromSeconds(20));
        await PollUntil(
            () => h.Strategy.Orders.GetPendingOrders().All(o => o.Id != exchangeId && o.Id != localId),
            cancelCts.Token);

        Assert.DoesNotContain(h.Strategy.Orders.GetPendingOrders(),
            o => o.Id == exchangeId || o.Id == localId);
    }

    // -----------------------------------------------------------------------
    // 3. TradeRegistryGroup_LifecycleAgainstPaper
    // -----------------------------------------------------------------------
    // Opens a TradeRegistry group (MKT BUY entry → SL + TP placed as individual orders →
    // ProtectionActive) then cancels the group (client-side OCO: both SL and TP cancelled).
    // Asserts entry fills, group reaches ProtectionActive, then group is Closed/Cancelled.
    // Bounded 45 s (MKT fill + protection submission + cancel round-trip).
    [Fact]
    public async Task TradeRegistryGroup_LifecycleAgainstPaper()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        await using var h = await Harness.ConnectAsync(Ct);
        var strategy = h.Strategy;
        var registry = strategy.TradeRegistry;

        var entryFillTcs = new TaskCompletionSource<Fill>(TaskCreationOptions.RunContinuationsAsynchronously);
        strategy.TradeCallback = (fill, order) =>
        {
            if (order.Side == OrderSide.Buy)
                entryFillTcs.TrySetResult(fill);
        };

        // SL: stop at $100 (far below any paper fill), TP: limit at $99 999 (never hits).
        var slTicks = AaplScale.FromMarketPrice(100.00m);
        var tpTicks  = AaplScale.FromMarketPrice(99_999.00m);

        var group = registry.OpenGroup(
            Aapl,
            OrderSide.Buy,
            OrderType.Market,
            quantity: 1m,
            slPrice: slTicks,
            tpLevels: [new TpLevel { Price = tpTicks, ClosurePercentage = 1m }],
            entryStopPrice: 0);
        Assert.NotNull(group);
        var groupId = group.GroupId;

        // Wait for the MKT entry to fill.
        await entryFillTcs.Task.WaitAsync(TimeSpan.FromSeconds(45), Ct);

        // Let the dispatcher's EventQueue settle so HandleEntryFill submits the protection orders.
        await Task.Delay(1500, Ct);

        var grp = registry.GetGroup(groupId);
        Assert.NotNull(grp);
        Assert.Equal(OrderGroupStatus.ProtectionActive, grp.Status);

        // Cancel the entire group: TradeRegistry.CancelGroup cancels SL and TP via IOrderContext.Cancel.
        registry.CancelGroup(groupId);

        // Poll until the group is no longer ProtectionActive (cancel acks processed, group Closed/Cancelled).
        using var cancelCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelCts.CancelAfter(TimeSpan.FromSeconds(20));
        await PollUntil(
            () =>
            {
                var g = registry.GetGroup(groupId);
                return g is null || g.Status is OrderGroupStatus.Closed or OrderGroupStatus.Cancelled;
            },
            cancelCts.Token);

        var final = registry.GetGroup(groupId);
        Assert.True(
            final is null || final.Status is OrderGroupStatus.Closed or OrderGroupStatus.Cancelled,
            $"Group should be gone/closed/cancelled after CancelGroup; actual: {final?.Status}");
    }

    // -----------------------------------------------------------------------
    // 4. ReconnectReconciliation
    // -----------------------------------------------------------------------
    // Place a resting LMT order, trigger the IB session reconnect path by firing
    // IbWrapper.ConnectionDropped (the same public event IbWrapper raises on a real TCP drop via
    // connectionClosed/error-1101), and assert the resting order SURVIVES reconciliation.
    //
    // IbSession wires OnConnectionDropped to this event. The reconnect worker runs Reconnect():
    //   (a) Connect (idempotent on a live socket), (b) re-issues subs, (c) fires Reconnected.
    // IbLiveConnector.OnSessionReconnected → ReconcileOnReconnect → SnapshotOpenOrders
    // (reqAllOpenOrders) → ReconcileFromSnapshot. The order is in the pending set (expected),
    // so it must NOT be orphan-cancelled.
    [Fact]
    public async Task ReconnectReconciliation()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        await using var h = await Harness.ConnectAsync(Ct);

        var limitTicks = AaplScale.FromMarketPrice(LimitFarBelowMarket);

        h.Strategy.Orders!.Submit(new Order
        {
            Id = 0,
            Asset = Aapl,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = 1m,
            LimitPrice = limitTicks,
        });

        // Wait for the order to appear as resting before triggering the reconnect.
        using var submitCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        submitCts.CancelAfter(TimeSpan.FromSeconds(20));
        await PollUntil(
            () => h.Strategy.Orders.GetPendingOrders().Count > 0,
            submitCts.Token);

        var restingId = h.Strategy.Orders.GetPendingOrders()[0].Id;

        // Trigger the reconnect path by calling connectionClosed() on the shared wrapper — the same
        // code path IB raises on a real TCP drop. IbSession.OnConnectionDropped writes to its _drops
        // channel; the worker runs Reconnect() → re-issues subs → fires Reconnected → ReconcileOnReconnect.
        h.Wrapper.connectionClosed();

        // Allow ample time for: reconnect handshake + reqAllOpenOrders pushback + reconciliation.
        await Task.Delay(12_000, Ct);

        // The resting order must still be present — expected, not orphan-cancelled.
        var afterPending = h.Strategy.Orders.GetPendingOrders();
        Assert.Contains(afterPending, o => o.Id == restingId);
    }

    // -----------------------------------------------------------------------
    // 5. SharedSocket_NoOrderStarvation
    // -----------------------------------------------------------------------
    // Subscribe to AAPL tick data AND concurrently place a MKT BUY on the same IbSession.
    // The off-pump order lane (IbOrderGateway's bounded channel + single worker) must deliver
    // the fill within 30 s even while the EReader pump thread is delivering tick callbacks.
    // Proves the "off-pump order lane" invariant: orders are NOT dispatched on the pump thread.
    [Fact]
    public async Task SharedSocket_NoOrderStarvation()
    {
        if (!IbPaperGatewayConfig.IsConfigured) Assert.Skip(IbPaperGatewayConfig.SkipReason);

        await using var h = await Harness.ConnectAsync(Ct);

        // Subscribe to AAPL ticks on the same session to load the EReader pump with callbacks.
        var tickAssetResolver = Substitute.For<IIbInstrumentAssetResolver>();
        tickAssetResolver.Resolve(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Asset>(Aapl));

        var tickConnector = new IbVenueConnector(
            h.Session,
            h.ContractResolver,
            tickAssetResolver,
            new IbDataPlaneOptions
            {
                InstrumentScales = { ["AAPL"] = new TickScale(PriceExp: 2, QtyExp: 0) },
            });

        using var tickDrainCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        tickDrainCts.CancelAfter(TimeSpan.FromSeconds(35)); // outlives the 30 s fill window

        // Drain ticks in the background — purely to exercise the pump while the order is in flight.
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in tickConnector.Stream(["AAPL"], tickDrainCts.Token)) { }
            }
            catch (OperationCanceledException) { /* expected */ }
        }, CancellationToken.None);

        // Brief pause to let the tick subscription wire up before placing the order.
        await Task.Delay(500, Ct);

        var fillTcs = new TaskCompletionSource<Fill>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Strategy.TradeCallback = (fill, _) => fillTcs.TrySetResult(fill);

        h.Strategy.Orders!.Submit(new Order
        {
            Id = 0,
            Asset = Aapl,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 1m,
        });

        // The off-pump lane must dispatch the fill promptly — not starved by the tick flood.
        var fill = await fillTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        tickDrainCts.Cancel(); // stop the background drain
        Assert.Equal(OrderSide.Buy, fill.Side);
        Assert.True(fill.Price > 0, "fill price must be positive despite concurrent tick load");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private static async Task PollUntil(Func<bool> cond, CancellationToken ct)
    {
        while (!cond())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
        }
    }

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------
    // Builds the full real order-plane stack over the paper gateway. The IbConnection stays
    // alive for the test duration (owned here, disposed in DisposeAsync).
    private sealed class Harness : IAsyncDisposable
    {
        private readonly IbConnection _connection;

        public IbLiveConnector Connector { get; }
        public HarnessStrategy Strategy { get; }
        public IIbMarketDataSession Session { get; }
        public IIbContractResolver ContractResolver { get; }
        public IbWrapper Wrapper { get; }

        private Harness(
            IbConnection connection,
            IbLiveConnector connector,
            HarnessStrategy strategy,
            IIbMarketDataSession session,
            IIbContractResolver contractResolver,
            IbWrapper wrapper)
        {
            _connection = connection;
            Connector = connector;
            Strategy = strategy;
            Session = session;
            ContractResolver = contractResolver;
            Wrapper = wrapper;
        }

        public static async Task<Harness> ConnectAsync(CancellationToken ct)
        {
            var wrapper = new IbWrapper();
            var connection = new IbConnection(wrapper, IbPaperGatewayConfig.Options);
            var session = new IbSession(
                new IbConnectionMarketDataClient(connection), wrapper, NullLogger<IbSession>.Instance);
            var detailsClient = new IbConnectionContractDetailsClient(connection, wrapper, TimeProvider.System);
            var contractResolver = new IbContractResolver(detailsClient);
            var summaryClient = new IbConnectionAccountSummaryClient(connection, wrapper);
            var loggerFactory = NullLoggerFactory.Instance;

            var strategy = new HarnessStrategy();
            var sessionConfig = new LiveSessionConfig
            {
                SessionId = Guid.NewGuid(),
                Strategy = strategy,
                AccountName = IbPaperGatewayConfig.AccountName,
                Subscriptions =
                [
                    new TimeBarSubscription("AAPL", "NASDAQ", DataFeedRole.Primary, TimeFrame.Parse("1h"))
                    { Asset = Aapl },
                ],
            };

            var connector = new IbLiveConnector(
                accountName: "ib",
                session: session,
                contractResolver: contractResolver,
                summaryClient: summaryClient,
                orderValidator: new OrderValidator(),
                tickRouter: new NoopTickRouter(),
                dispatch: new NoopStrategyDispatch(),
                options: new LiveDispatcherOptions(1024, 4096, TimeSpan.FromSeconds(30)),
                loggerFactory: loggerFactory,
                gatewayFactory: onReport => new IbOrderGateway(
                    new IbConnectionOrderClient(connection),
                    wrapper,
                    onReport,
                    loggerFactory.CreateLogger<IbOrderGateway>()));

            await connector.ConnectAsync(ct);
            await connector.AddSessionAsync(sessionConfig, ct);

            return new Harness(connection, connector, strategy, session, contractResolver, wrapper);
        }

        public async ValueTask DisposeAsync()
        {
            // StopAsync → AccountTarget.DisposeAsync cancel-alls open orders so re-runs start clean.
            await Connector.StopAsync();
            await Connector.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // HarnessStrategy
    // -----------------------------------------------------------------------
    // Minimal IOrderContextReceiver + ITradeRegistryProvider. Per-test callbacks are wired via
    // TradeCallback so each test observes only what it needs.
    private sealed class HarnessStrategy : IInt64BarStrategy, IOrderContextReceiver, ITradeRegistryProvider
    {
        private IOrderContext? _orders;
        private TradeRegistryModule? _registry;

        public string Version => "1.0.0";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];

        public IOrderContext? Orders => _orders;
        public TradeRegistryModule TradeRegistry => _registry ??= BuildRegistry();

        // Wired per-test so each test can hook the callback it needs:
        public Action<Fill, Order>? TradeCallback { get; set; }

        public void SetOrderContext(IOrderContext context)
        {
            _orders = context;
            _registry?.SetOrderContext(context);
        }

        public void OnInit() { }

        public void OnTrade(Fill fill, Order order) => TradeCallback?.Invoke(fill, order);

        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) { }

        private TradeRegistryModule BuildRegistry()
        {
            var r = new TradeRegistryModule(new TradeRegistryParams());
            if (_orders is not null)
                r.SetOrderContext(_orders);
            return r;
        }
    }

    // -----------------------------------------------------------------------
    // No-op infrastructure (only the IB transport is real in these tests)
    // -----------------------------------------------------------------------
    private sealed class NoopTickRouter : ITickRouter
    {
        public void Publish(string instrument, in TradeTick tick) { }

        public ValueTask EnsureSources(LiveSessionRegistration reg, Func<string, ScaleContext> scaleFor) =>
            ValueTask.CompletedTask;

        public IReadOnlyList<Int64Bar> RecentBars(string instrument, BarSpecKey spec) => [];

        public ValueTask RemoveSources(Guid sessionId) => ValueTask.CompletedTask;
    }

    private sealed class NoopStrategyDispatch : IStrategyDispatch
    {
        public void Register(LiveSessionRegistration registration) { }
        public void Unregister(Guid sessionId) { }
        public void DispatchBar(string instrument, BarSpecKey spec, in Int64Bar bar, bool isStart) { }
        public void DispatchTick(string instrument, in TradeTick tick) { }
    }
}
