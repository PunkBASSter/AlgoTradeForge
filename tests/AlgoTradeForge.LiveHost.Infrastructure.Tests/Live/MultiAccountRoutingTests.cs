using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

// ---------------------------------------------------------------------------
// Fakes
// ---------------------------------------------------------------------------

internal sealed class InMemoryAccountTarget : IAccountTarget
{
    private readonly LiveOrderContext _ctx;

    public string AccountName { get; }
    public Portfolio Portfolio { get; }
    public bool Disposed { get; private set; }

    public InMemoryAccountTarget(string name, IExchangeOrderClient client, long initialCash)
    {
        AccountName = name;
        Portfolio = new Portfolio { InitialCash = initialCash };
        Portfolio.Initialize();
        _ctx = new LiveOrderContext(Portfolio, new OrderValidator(), NullLogger.Instance, client);
        _ctx.Start(CancellationToken.None);
    }

    internal LiveOrderContext Context => _ctx;

    public IOrderContext OrderContextFor(Guid sessionId) =>
        new SessionOrderContext(sessionId, _ctx);

    public async ValueTask DisposeAsync()
    {
        if (Disposed) return;
        Disposed = true;
        await _ctx.StopAsync();
    }
}

internal sealed class FakeAccountTargetFactory(
    params (string Account, IExchangeOrderClient Client, long Cash)[] accounts)
    : IAccountTargetFactory
{
    public Task<IAccountTarget> Create(string account, Asset executionAsset, CancellationToken ct = default)
    {
        var (_, client, cash) = accounts.First(a => a.Account == account);
        return Task.FromResult<IAccountTarget>(new InMemoryAccountTarget(account, client, cash));
    }
}

// ---------------------------------------------------------------------------
// Acceptance suite
// ---------------------------------------------------------------------------

public class MultiAccountRoutingTests
{
    private static readonly CryptoAsset BtcUsdt = CryptoAsset.Create("BTCUSDT", "Binance",
        decimalDigits: 2,
        minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    private static async Task Poll(Func<bool> cond)
    {
        for (var i = 0; i < 100 && !cond(); i++)
            await Task.Delay(10);
    }

    /// <summary>
    /// Headline acceptance: two distinct accounts use isolated exchange clients and isolated
    /// portfolio ledgers. An order on account A never touches account B's client.
    /// </summary>
    [Fact]
    public async Task TwoAccounts_OrdersIsolated_PortfoliosIsolated()
    {
        var ct = TestContext.Current.CancellationToken;

        var clientA = Substitute.For<IExchangeOrderClient>();
        clientA.PlaceOrderAsync(Arg.Any<string>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
                Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(new ExchangeOrderResult(1L, []));

        var clientB = Substitute.For<IExchangeOrderClient>();
        clientB.PlaceOrderAsync(Arg.Any<string>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
                Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(new ExchangeOrderResult(2L, []));

        var factory = new FakeAccountTargetFactory(
            ("A", clientA, 100_000_00L),
            ("B", clientB, 50_000_00L));

        var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

        var targetA = await router.ResolveTarget("A", BtcUsdt, ct);
        var targetB = await router.ResolveTarget("B", BtcUsdt, ct);

        var sessX = Guid.NewGuid();
        var ctxX = targetA.OrderContextFor(sessX);
        ctxX.Submit(new Order
        {
            Id = 0,
            Asset = BtcUsdt,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = 0.001m,
            LimitPrice = 5000000L,
        });

        await Poll(() => clientA.ReceivedCalls().Any());

        await clientA.Received().PlaceOrderAsync(
            "BTCUSDT", OrderSide.Buy, OrderType.Limit,
            0.001m, Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>());

        // Account B's client must not have been touched.
        Assert.Empty(clientB.ReceivedCalls());

        // Each account has its own independent portfolio ledger.
        Assert.NotEqual(targetA.Portfolio.InitialCash, targetB.Portfolio.InitialCash);

        await targetA.DisposeAsync();
        await targetB.DisposeAsync();
    }

    /// <summary>
    /// Co-tenancy: two sessions share the same account target (same Portfolio), but each order is
    /// tagged with the originating session id. Fills applied concurrently from both sessions end up
    /// in the shared portfolio with no torn state.
    /// </summary>
    [Fact]
    public async Task CoTenant_SharedPortfolio_AndPerSessionOrderTagging()
    {
        var ct = TestContext.Current.CancellationToken;

        var client = Substitute.For<IExchangeOrderClient>();
        long nextExchangeId = 100L;
        client.PlaceOrderAsync(Arg.Any<string>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
                Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ExchangeOrderResult(
                Interlocked.Increment(ref nextExchangeId), []));

        var factory = new FakeAccountTargetFactory(("A", client, 200_000_00L));
        var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

        // Resolve same account twice — should return the same target (refcount 2).
        var targetA1 = await router.ResolveTarget("A", BtcUsdt, ct);
        var targetA2 = await router.ResolveTarget("A", BtcUsdt, ct);
        Assert.Same(targetA1, targetA2);

        var target = (InMemoryAccountTarget)targetA1;

        // Wire the OrderMapped event so the router tracks exchange-id→session.
        target.Context.OrderMapped += (exId, sId) => router.TrackOrder(exId, sId);

        var sessX = Guid.NewGuid();
        var sessY = Guid.NewGuid();

        var ctxX = targetA1.OrderContextFor(sessX);
        var ctxY = targetA2.OrderContextFor(sessY);

        // Submit one order per session.
        var localIdX = ctxX.Submit(new Order
        {
            Id = 0,
            Asset = BtcUsdt,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = 0.001m,
            LimitPrice = 5000000L,
        });

        var localIdY = ctxY.Submit(new Order
        {
            Id = 0,
            Asset = BtcUsdt,
            Side = OrderSide.Sell,
            Type = OrderType.Limit,
            Quantity = 0.002m,
            LimitPrice = 5100000L,
        });

        // Wait until the background channel drains both orders.
        // Once PlaceOrderAsync has returned twice, both re-keys (and the OrderMapped -> TrackOrder
        // callbacks they fire) have already run on the same drain continuation, so the order->session
        // map is populated. If the re-key ever moves off the drain task, wait on OrderMapped explicitly.
        await Poll(() => client.ReceivedCalls().Count() >= 2);

        // Both sessions read the same shared portfolio object.
        Assert.Same(((InMemoryAccountTarget)targetA1).Portfolio,
            ((InMemoryAccountTarget)targetA2).Portfolio);

        // Per-session order routing: each exchange id resolves to its submitter.
        // The exchange ids were assigned by PlaceOrderAsync (101, 102 in order of drain).
        // We don't know which session got which exchange id, so we verify via the router's map.
        Assert.True(router.TryResolveSession(101L, out var resolved1));
        Assert.True(router.TryResolveSession(102L, out var resolved2));

        // The two resolved sessions must be different and both known.
        Assert.NotEqual(resolved1, resolved2);
        var knownSessions = new List<Guid> { sessX, sessY };
        Assert.Contains(resolved1, knownSessions);
        Assert.Contains(resolved2, knownSessions);

        // Concurrency stress: add fills from both sessions concurrently into the shared context.
        var fillTasks = Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => target.Context.AddFill(new Fill(
                OrderId: i + 1000L,
                Asset: BtcUsdt,
                Timestamp: DateTimeOffset.UtcNow,
                Price: 5000000L,
                Quantity: 0.001m,
                Side: i % 2 == 0 ? OrderSide.Buy : OrderSide.Sell,
                Commission: 0L)), ct)).ToArray();

        await Task.WhenAll(fillTasks);

        // Shared portfolio must reflect all 20 fills without torn state.
        var allFills = target.Context.GetAllFills();
        // The ring buffer capacity is 1000; we added 20, so all should be present
        // (plus any earlier REST fills — filter to our range).
        var stressFills = allFills.Where(f => f.OrderId >= 1000L).ToList();
        Assert.Equal(20, stressFills.Count);

        // Drain the refcount to 0 so the real LiveOrderContext loop is stopped (no leak).
        // Lifecycle assertions live in Target_DisposedOnly_OnLastSessionRelease, not here.
        await router.ReleaseTarget("A", ct);
        await router.ReleaseTarget("A", ct);
    }

    /// <summary>
    /// Funds discovery: the factory seeds each account's portfolio from a configured per-account
    /// balance (simulating discovered funds, not any operator config). Resolving the same account
    /// twice returns the cached target (no re-seed).
    /// </summary>
    [Fact]
    public async Task NewAccount_PortfolioSeeded_FromDiscoveredFunds()
    {
        var ct = TestContext.Current.CancellationToken;

        const long discoveredCash = 12_345_00L;

        var client = Substitute.For<IExchangeOrderClient>();
        var factory = new FakeAccountTargetFactory(("A", client, discoveredCash));
        var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

        var target = await router.ResolveTarget("A", BtcUsdt, ct);

        // Portfolio is seeded from the factory's discovered funds.
        Assert.Equal(discoveredCash, target.Portfolio.InitialCash);

        // Resolving again returns the same cached target (factory called only once).
        var target2 = await router.ResolveTarget("A", BtcUsdt, ct);
        Assert.Same(target, target2);

        // InitialCash is unchanged (no re-seed on second resolve).
        Assert.Equal(discoveredCash, target2.Portfolio.InitialCash);

        await ((InMemoryAccountTarget)target).DisposeAsync();
    }

    /// <summary>
    /// FIX #3 regression: when a co-tenant session leaves, only ITS resting orders are cancelled.
    /// Two sessions on the same account each place an order; the filter loop (mirroring
    /// RemoveSessionAsync) cancels only the leaving session's order, matched via the router's
    /// order->session map. The co-tenant's order stays live on the exchange.
    /// </summary>
    [Fact]
    public async Task CoTenant_RemoveSession_CancelsOnlyLeavingSessionsOrders()
    {
        var ct = TestContext.Current.CancellationToken;

        var client = Substitute.For<IExchangeOrderClient>();
        long nextExchangeId = 100L;
        client.PlaceOrderAsync(Arg.Any<string>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
                Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ExchangeOrderResult(Interlocked.Increment(ref nextExchangeId), []));

        var factory = new FakeAccountTargetFactory(("A", client, 200_000_00L));
        var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

        var target = (InMemoryAccountTarget)await router.ResolveTarget("A", BtcUsdt, ct);
        var ctx = target.Context;
        ctx.OrderMapped += (exId, sId) => router.TrackOrder(exId, sId);

        var sessA = Guid.NewGuid();
        var sessB = Guid.NewGuid();

        target.OrderContextFor(sessA).Submit(new Order
        {
            Id = 0, Asset = BtcUsdt, Side = OrderSide.Buy, Type = OrderType.Limit,
            Quantity = 0.001m, LimitPrice = 5000000L,
        });
        target.OrderContextFor(sessB).Submit(new Order
        {
            Id = 0, Asset = BtcUsdt, Side = OrderSide.Sell, Type = OrderType.Limit,
            Quantity = 0.002m, LimitPrice = 5100000L,
        });

        // Wait for both to be placed (re-keyed to exchange ids + OrderMapped -> TrackOrder fired).
        await Poll(() => client.ReceivedCalls().Count() >= 2);
        await Poll(() => ctx.GetPendingOrders().Count(o => o.Id > 0) >= 2);

        // Capture exchange ids before cancelling (Cancel removes them from the pending set).
        var sessAExchangeId = ctx.GetPendingOrders()
            .Select(o => o.Id)
            .Single(id => router.TryResolveSession(id, out var s) && s == sessA);
        var sessBExchangeId = ctx.GetPendingOrders()
            .Select(o => o.Id)
            .Single(id => router.TryResolveSession(id, out var s) && s == sessB);

        // Mirror RemoveSessionAsync's filter: cancel only sessA's resting orders.
        foreach (var order in ctx.GetPendingOrders())
            if (router.TryResolveSession(order.Id, out var owner) && owner == sessA)
                ctx.Cancel(order.Id);

        await Poll(() => client.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IExchangeOrderClient.CancelOrderAsync)));

        await client.Received(1).CancelOrderAsync("BTCUSDT", sessAExchangeId, Arg.Any<CancellationToken>());
        await client.DidNotReceive().CancelOrderAsync("BTCUSDT", sessBExchangeId, Arg.Any<CancellationToken>());

        await router.ReleaseTarget("A", ct);
    }

    /// <summary>
    /// Concurrent read-during-write: one task floods AddFill while another reads AvailableMargin
    /// and iterates GetPositions(). Without locking reads through _recentFillsLock, this throws
    /// InvalidOperationException ("collection was modified") or returns torn margin. With the fix,
    /// it completes cleanly and the final net position equals the sum of fills.
    /// </summary>
    [Fact]
    public async Task CoTenant_ConcurrentReadDuringFill_NoTornStateOrThrow()
    {
        var ct = TestContext.Current.CancellationToken;

        var client = Substitute.For<IExchangeOrderClient>();
        var target = new InMemoryAccountTarget("A", client, 500_000_00L);
        var ctx = target.Context;

        const int iterations = 300;

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
                ctx.AddFill(new Fill(
                    OrderId: i + 1L,
                    Asset: BtcUsdt,
                    Timestamp: DateTimeOffset.UtcNow,
                    Price: 5_000_000L,
                    Quantity: 0.001m,
                    Side: OrderSide.Buy,
                    Commission: 0L));
        }, ct);

        var reader = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                _ = ctx.AvailableMargin;
                foreach (var _ in ctx.GetPositions()) { }
            }
        }, ct);

        // Pre-fix: one of these tasks would throw InvalidOperationException.
        await Task.WhenAll(writer, reader);

        // All fills must be recorded.
        var allFills = ctx.GetAllFills();
        Assert.Equal(iterations, allFills.Count);

        // The position for BTCUSDT must reflect all buy fills (net quantity = iterations * 0.001m).
        var positions = ctx.GetPositions();
        Assert.True(positions.ContainsKey(BtcUsdt.Name));
        Assert.Equal(iterations * 0.001m, positions[BtcUsdt.Name].Quantity);

        await target.DisposeAsync();
    }

    /// <summary>
    /// Reference-counted lifecycle: the target is disposed only when the last resolve is released.
    /// One release with refcount 2 leaves the target alive; the second release disposes it and
    /// removes it from the router's live set.
    /// </summary>
    [Fact]
    public async Task Target_DisposedOnly_OnLastSessionRelease()
    {
        var ct = TestContext.Current.CancellationToken;

        var client = Substitute.For<IExchangeOrderClient>();
        var factory = new FakeAccountTargetFactory(("A", client, 100_000_00L));
        var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

        // Acquire twice: refcount = 2.
        var target1 = await router.ResolveTarget("A", BtcUsdt, ct);
        var target2 = await router.ResolveTarget("A", BtcUsdt, ct);
        Assert.Same(target1, target2);

        var target = (InMemoryAccountTarget)target1;

        // First release: refcount drops to 1 — target must NOT be disposed yet.
        await router.ReleaseTarget("A", ct);
        Assert.False(target.Disposed);
        Assert.Single(router.Targets);

        // Second release: refcount drops to 0 — target IS disposed and evicted.
        await router.ReleaseTarget("A", ct);
        Assert.True(target.Disposed);
        Assert.Empty(router.Targets);
    }
}
