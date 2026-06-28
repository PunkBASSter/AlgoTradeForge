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

    // -----------------------------------------------------------------------
    // Fixture + fakes (mirrors MultiAccountRoutingTests / LiveSessionDispatcherTests)
    // -----------------------------------------------------------------------

    private sealed class UnionReconcileFixture : IAsyncDisposable
    {
        public required LiveSessionDispatcher Dispatcher { get; init; }
        public required string Account { get; init; }
        public required List<long> CancelledOrderIds { get; init; }
        public required CancellationTokenSource Cts { get; init; }

        private const string Account_ = "A";
        private const string QuoteAsset = "USDT";

        private static readonly CryptoAsset Btc = CryptoAsset.Create("BTCUSDT", "Binance",
            decimalDigits: 2,
            minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

        private static async Task Poll(Func<bool> cond)
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
            await OpenGroupAndPlaceSl(stratA, slPrice: 40_000_00L);
            await Poll(() => stratA.PendingOrderIds.Contains(1001L));
            await OpenGroupAndPlaceSl(stratB, slPrice: 41_000_00L);
            await Poll(() => stratA.PendingOrderIds.Contains(2002L)); // shared ledger → either strat sees it

            return new UnionReconcileFixture
            {
                Dispatcher = dispatcher,
                Account = Account_,
                CancelledOrderIds = cancelled,
                Cts = cts,
            };
        }

        private static LiveSessionConfig Config(IInt64BarStrategy strategy) => new()
        {
            SessionId = Guid.NewGuid(),
            Strategy = strategy,
            AccountName = Account_,
            Subscriptions =
            [
                new TimeBarSubscription(Btc.Name, "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))
                    { Asset = Btc },
            ],
        };

        // Opens a group (market entry) and fills the entry so the registry places its SL, then waits for the
        // SL to re-key to its exchange id on the shared account ledger.
        private static async Task OpenGroupAndPlaceSl(RegistryStrategy strat, long slPrice)
        {
            // Fill exactly the group just opened (a co-tenant may have an unrelated id-advancing group active).
            var entryOrderId = strat.OpenGroup(Btc, slPrice);
            strat.OnTrade(
                new Fill(entryOrderId, Btc, DateTimeOffset.UtcNow, 40_500_00L, 0.001m, OrderSide.Buy, 0L),
                new Order { Id = entryOrderId, Asset = Btc, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 0.001m });

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
