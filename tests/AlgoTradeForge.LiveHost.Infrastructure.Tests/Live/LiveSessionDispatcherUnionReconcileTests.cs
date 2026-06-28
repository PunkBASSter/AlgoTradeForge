using System.Collections.Concurrent;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

// The #8 co-tenancy guard: account-wide open-order pushback diffed against the UNION of every
// co-tenant session's expected orders — never one session's registry — so a reconcile triggered by
// one session can't cancel a co-tenant's live protective order.
public sealed class LiveSessionDispatcherUnionReconcileTests
{
    [Fact]
    public async Task ReconcileFromSnapshot_DoesNotCancelCoTenantWorkingOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await UnionReconcileFixture.TwoSessionsOnOneAccount(ct);

        // session A expects SL #1001; session B expects SL #2002; broker pushback has both + a true orphan #3003
        await f.Dispatcher.ReconcileFromSnapshot(f.Account, [1001, 2002, 3003], ct);

        Assert.DoesNotContain(1001, f.CancelledOrderIds); // A's order survives
        Assert.DoesNotContain(2002, f.CancelledOrderIds); // B's order survives
        Assert.Contains(3003, f.CancelledOrderIds);        // only the true orphan is cancelled

        await f.DisposeAsync();
    }

    // The #6 per-symbol reconcile: two same-tick sessions trading DIFFERENT symbols co-tenant one account.
    // The periodic reconcile must query EACH symbol's open orders — pre-fix it queried only the seed symbol,
    // so the non-seed symbol's SL looked missing and got re-submitted every cycle.
    [Fact]
    public async Task PeriodicReconcile_QueriesEachCoTenantSymbol_AndPreservesTheirOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await UnionReconcileFixture.TwoSymbolsOnOneAccount(ct);

        f.Dispatcher.StartReconciliation();

        await UnionReconcileFixture.Poll(() =>
            f.QueriedSymbols.Contains("BTCUSDT") && f.QueriedSymbols.Contains("ETHUSDT"));

        Assert.Contains("BTCUSDT", f.QueriedSymbols); // seed symbol queried
        Assert.Contains("ETHUSDT", f.QueriedSymbols); // non-seed co-tenant symbol ALSO queried (the fix)
        Assert.Empty(f.CancelledOrderIds);            // each SL present under its own symbol → no orphan cancels
        Assert.Equal(2, f.PlacedStops.Count);         // no duplicate SL re-submission

        await f.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Fixture + fakes (mirrors MultiAccountRoutingTests / LiveSessionDispatcherTests)
    // -----------------------------------------------------------------------

    private sealed class UnionReconcileFixture : IAsyncDisposable
    {
        public required LiveSessionDispatcher Dispatcher { get; init; }
        public required string Account { get; init; }
        public required List<long> CancelledOrderIds { get; init; }
        public required CancellationTokenSource Cts { get; init; }

        // Periodic per-symbol reconcile observability: symbols queried via GetOpenOrdersAsync and every
        // stop-loss placement (to detect duplicate re-submission of a co-tenant symbol's protective order).
        public ConcurrentBag<string> QueriedSymbols { get; init; } = [];
        public List<long> PlacedStops { get; init; } = [];

        private const string Account_ = "A";
        private const string QuoteAsset = "USDT";

        private static readonly CryptoAsset Btc = CryptoAsset.Create("BTCUSDT", "Binance",
            decimalDigits: 2,
            minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

        // Same tick (decimalDigits) + same quote (USDT) as Btc, so it legally co-tenants one account
        // despite a different symbol — the case the per-symbol reconcile must handle.
        private static readonly CryptoAsset Eth = CryptoAsset.Create("ETHUSDT", "Binance",
            decimalDigits: 2,
            minOrderQuantity: 0.0001m, maxOrderQuantity: 9000m, quantityStepSize: 0.0001m);

        public static async Task Poll(Func<bool> cond)
        {
            for (var i = 0; i < 200 && !cond(); i++)
                await Task.Delay(10);
        }

        public static async Task<UnionReconcileFixture> TwoSessionsOnOneAccount(CancellationToken ct)
        {
            var cancelled = new List<long>();

            // One shared exchange client backs the account. The stop-loss (the protective order the union
            // reconcile must preserve) is assigned a deterministic exchange id per session — 1001 for A's SL,
            // 2002 for B's SL — regardless of the entry/TP placement order; everything else gets a throwaway
            // id. CancelOrderAsync records what the reconcile cancels.
            var client = Substitute.For<IExchangeOrderClient>();
            var slIds = new Queue<long>([1001L, 2002L]);
            var throwaway = 9_000L;
            client.PlaceOrderAsync(Arg.Any<string>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
                    Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var type = ci.ArgAt<OrderType>(2);
                    var id = type == OrderType.Stop ? slIds.Dequeue() : Interlocked.Increment(ref throwaway);
                    return new ExchangeOrderResult(id, []);
                });
            client.CancelOrderAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(ci => { lock (cancelled) cancelled.Add(ci.ArgAt<long>(1)); return Task.CompletedTask; });

            var factory = new FixedFundsTargetFactory(client, 1_000_000_00L, QuoteAsset);
            var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);
            var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);
            var cts = new CancellationTokenSource();

            var dispatcher = new LiveSessionDispatcher(
                router, new NoopMarketDataSource(), new NoopStrategyDispatch(), reconciler,
                new LiveDispatcherOptions(1024, 4096, TimeSpan.FromSeconds(30)),
                NullLogger.Instance);
            dispatcher.Start(cts.Token);

            var stratA = new RegistryStrategy();
            var stratB = new RegistryStrategy();
            await dispatcher.AddSession(Config(stratA), QuoteAsset, ct);
            await dispatcher.AddSession(Config(stratB), QuoteAsset, ct);

            // Each TradeRegistryModule allocates from the SAME negative id base (-1_000_000), so two co-tenant
            // modules sharing one account ledger would collide in _localToExchangeId. Advance B's module id
            // space first so the two sessions' protective orders carry distinct local ids — the realistic case
            // where each session's expected SL maps to its own exchange id. (Co-tenant module-id collision on a
            // shared ledger is a separate concern noted in the E1 report.)
            stratB.OpenIdAdvancingGroup(Btc);

            // Drive each session's TradeRegistry to an active group with a placed SL. The SL re-keys to the
            // queued exchange id (1001 then 2002), populating the shared account ledger's local→exchange map.
            // Wait on the SHARED ledger's pending orders (keyed by exchange id after re-key), not the placement
            // queue — the re-key runs AFTER PlaceOrderAsync dequeues, so the queue draining alone races it.
            await OpenGroupAndPlaceSl(stratA, Btc, slPrice: 40_000_00L);
            await Poll(() => stratA.PendingOrderIds.Contains(1001L));
            await OpenGroupAndPlaceSl(stratB, Btc, slPrice: 41_000_00L);
            await Poll(() => stratA.PendingOrderIds.Contains(2002L)); // shared ledger → either strat sees it

            return new UnionReconcileFixture
            {
                Dispatcher = dispatcher,
                Account = Account_,
                CancelledOrderIds = cancelled,
                Cts = cts,
            };
        }

        // Two co-tenant sessions on ONE account trading DIFFERENT symbols (same tick + quote). Each holds a
        // resting SL; GetOpenOrdersAsync returns each symbol's own SL. Drives the periodic per-symbol reconcile.
        public static async Task<UnionReconcileFixture> TwoSymbolsOnOneAccount(CancellationToken ct)
        {
            var cancelled = new List<long>();
            var queried = new ConcurrentBag<string>();
            var placedStops = new List<long>();

            var client = Substitute.For<IExchangeOrderClient>();
            var throwaway = 9_000L;
            client.PlaceOrderAsync(Arg.Any<string>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
                    Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var symbol = ci.ArgAt<string>(0);
                    if (ci.ArgAt<OrderType>(2) == OrderType.Stop)
                    {
                        var slId = symbol == Btc.Name ? 1001L : 2002L;
                        lock (placedStops) placedStops.Add(slId);
                        return new ExchangeOrderResult(slId, []);
                    }
                    return new ExchangeOrderResult(Interlocked.Increment(ref throwaway), []);
                });
            client.GetOpenOrdersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var symbol = ci.ArgAt<string>(0);
                    queried.Add(symbol);
                    // Each symbol's own SL is resting at the broker → not missing, not orphaned.
                    long slId = symbol == Btc.Name ? 1001L : symbol == Eth.Name ? 2002L : 0L;
                    IReadOnlyList<ExchangeOpenOrder> open = slId == 0L
                        ? []
                        : [new ExchangeOpenOrder(slId, symbol, "SELL", "STOP", 0.001m, 0, 0, "Submitted")];
                    return Task.FromResult(open);
                });
            client.CancelOrderAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(ci => { lock (cancelled) cancelled.Add(ci.ArgAt<long>(1)); return Task.CompletedTask; });

            var factory = new FixedFundsTargetFactory(client, 1_000_000_00L, QuoteAsset);
            var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);
            var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);
            var cts = new CancellationTokenSource();

            var dispatcher = new LiveSessionDispatcher(
                router, new NoopMarketDataSource(), new NoopStrategyDispatch(), reconciler,
                new LiveDispatcherOptions(1024, 4096, TimeSpan.FromMilliseconds(50)),
                NullLogger.Instance);
            dispatcher.Start(cts.Token);

            var stratBtc = new RegistryStrategy();
            var stratEth = new RegistryStrategy();
            await dispatcher.AddSession(Config(stratBtc, Btc), QuoteAsset, ct);
            await dispatcher.AddSession(Config(stratEth, Eth), QuoteAsset, ct);

            stratEth.OpenIdAdvancingGroup(Eth); // distinct module-id space on the shared ledger (see note above)

            await OpenGroupAndPlaceSl(stratBtc, Btc, slPrice: 40_000_00L);
            await Poll(() => stratBtc.PendingOrderIds.Contains(1001L));
            await OpenGroupAndPlaceSl(stratEth, Eth, slPrice: 41_000_00L);
            await Poll(() => stratBtc.PendingOrderIds.Contains(2002L));

            return new UnionReconcileFixture
            {
                Dispatcher = dispatcher,
                Account = Account_,
                CancelledOrderIds = cancelled,
                Cts = cts,
                QueriedSymbols = queried,
                PlacedStops = placedStops,
            };
        }

        private static LiveSessionConfig Config(IInt64BarStrategy strategy) => Config(strategy, Btc);

        private static LiveSessionConfig Config(IInt64BarStrategy strategy, Asset asset) => new()
        {
            SessionId = Guid.NewGuid(),
            Strategy = strategy,
            AccountName = Account_,
            Subscriptions =
            [
                new TimeBarSubscription(asset.Name, "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))
                    { Asset = asset },
            ],
        };

        // Opens a group (market entry) and fills the entry so the registry places its SL, then waits for the
        // SL to re-key to its exchange id on the shared account ledger.
        private static async Task OpenGroupAndPlaceSl(RegistryStrategy strat, Asset asset, long slPrice)
        {
            // Fill exactly the group just opened (a co-tenant may have an unrelated id-advancing group active).
            var entryOrderId = strat.OpenGroup(asset, slPrice);
            strat.OnTrade(
                new Fill(entryOrderId, asset, DateTimeOffset.UtcNow, 40_500_00L, 0.001m, OrderSide.Buy, 0L),
                new Order { Id = entryOrderId, Asset = asset, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 0.001m });

            await Poll(() => strat.Registry.GetExpectedOrders()
                .Any(e => e.Type == ExpectedOrderType.StopLoss));
        }

        public async ValueTask DisposeAsync()
        {
            await Dispatcher.Stop(Cts.Token);
            Cts.Dispose();
        }
    }

    // Minimal ITradeRegistryProvider strategy: forwards the session order context to its TradeRegistry so
    // OpenGroup/OnTrade place real orders through the shared account ledger.
    private sealed class RegistryStrategy : IInt64BarStrategy, IOrderContextReceiver, ITradeRegistryProvider
    {
        public TradeRegistryModule Registry { get; } = new(new TradeRegistryParams());
        private IOrderContext? _orders;

        public RegistryStrategy() => Registry.SetEventBus(NullEventBus.Instance);

        public TradeRegistryModule TradeRegistry => Registry;
        public string Version => "1.0.0";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];

        // The shared account ledger's pending order ids (keyed by exchange id once re-keyed).
        public IReadOnlyCollection<long> PendingOrderIds =>
            _orders!.GetPendingOrders().Select(o => o.Id).ToList();

        public void SetOrderContext(IOrderContext context)
        {
            _orders = context;
            Registry.SetOrderContext(context);
        }

        // Opens a group but never fills its entry — consumes the module's entry id (advancing its negative id
        // counter) without placing protective orders, so a co-tenant module's protective ids stay distinct.
        public void OpenIdAdvancingGroup(Asset asset)
        {
            Registry.SetOrderContext(_orders!);
            Registry.OpenGroup(asset, OrderSide.Buy, OrderType.Market,
                quantity: 0.001m, slPrice: 1_00L, tpLevels: []);
        }

        public long OpenGroup(Asset asset, long slPrice)
        {
            Registry.SetOrderContext(_orders!);
            // SL-only group: the union reconcile must preserve exactly this protective order.
            var group = Registry.OpenGroup(asset, OrderSide.Buy, OrderType.Market,
                quantity: 0.001m, slPrice: slPrice, tpLevels: []);
            return group!.EntryOrderId;
        }

        public void OnInit() { }
        public void OnTrade(Fill fill, Order order)
        {
            Registry.SetOrderContext(_orders!);
            Registry.OnFill(fill, order);
        }
        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) { }
    }

    private sealed class FixedFundsTargetFactory(IExchangeOrderClient client, long cash, string quoteAsset)
        : IAccountTargetFactory
    {
        public Task<IAccountTarget> Create(string account, Asset executionAsset, CancellationToken ct = default)
        {
            var portfolio = new Portfolio { InitialCash = cash };
            portfolio.Initialize();
            var ctx = new LiveOrderContext(portfolio, new OrderValidator(), NullLogger.Instance, client);
            ctx.Start(CancellationToken.None);
            return Task.FromResult<IAccountTarget>(
                new AccountTarget(account, portfolio, ctx, client, executionAsset, quoteAsset, NullLogger.Instance));
        }
    }

    private sealed class NoopMarketDataSource : IMarketDataSource
    {
        public void Register(LiveSessionRegistration registration) { }
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
