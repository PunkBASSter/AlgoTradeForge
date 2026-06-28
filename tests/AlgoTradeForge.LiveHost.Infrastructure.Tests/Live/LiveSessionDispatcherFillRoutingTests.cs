using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
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

// Covers the money-path bug E1 surfaced: in live, a fill arrives keyed by the EXCHANGE id, but a
// strategy MODULE (TradeRegistryModule) keys _orderToGroup by its OWN (negative) order id. Without the
// re-stamp the module's OnFill misses, PlaceProtectiveOrders never fires, and SL/TP are never placed.
// This drives a REAL entry fill through the REAL exchange-id push path into a REAL TradeRegistryModule.
public sealed class LiveSessionDispatcherFillRoutingTests
{
    [Fact]
    public async Task EntryFill_OnExchangeIdPath_ReachesModule_AndPlacesProtectiveOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = await Fixture.WithRegistrySession(ct);

        // Module submits a stop entry with its own negative id (-1_000_001). Placement re-keys it to
        // the exchange id and fires OrderMapped → TrackOrder, so the trade report below routes.
        fixture.Strategy.OpenEntry(slPrice: 4_900_000L, entryStopPrice: 5_100_000L);
        await fixture.WaitForOrderMapped(ct);

        // The entry's negative id must now resolve to the exchange id, and the group is still PendingEntry.
        var group = fixture.Strategy.Module.ActiveGroups.Single();
        Assert.Equal(OrderGroupStatus.PendingEntry, group.Status);
        Assert.Equal(0L, group.SlOrderId);

        // Deliver the entry FILL keyed by the EXCHANGE id (the only id the venue reports).
        fixture.Dispatcher.OnExecutionReport(new ExecutionReport(
            OrderId: fixture.ExchangeOrderId,
            Asset: fixture.Asset,
            Side: OrderSide.Buy,
            ExecType: ExecType.Trade,
            LastFillPrice: 51_000m,
            LastFillQty: 0.001m,
            Commission: 0m,
            Status: OrderStatus.Filled,
            TransactionTime: DateTimeOffset.UnixEpoch,
            Type: OrderType.Stop,
            OriginalQuantity: 0.001m));

        await fixture.WaitForProtection(group, ct);

        // Headline assertion: the entry fill reached the module — it transitioned to ProtectionActive
        // and submitted a protective SL. Before the re-stamp fix the module never sees the fill (the
        // Fill carries the exchange id, _orderToGroup misses) and the group stays PendingEntry.
        Assert.Equal(OrderGroupStatus.ProtectionActive, group.Status);
        Assert.NotEqual(0L, group.SlOrderId);

        await fixture.DisposeAsync();
    }

    // Minimal IInt64BarStrategy that wraps a REAL TradeRegistryModule (mirrors StrategyBase's wiring:
    // OnTrade → module.OnFill, SetOrderContext → module.SetOrderContext).
    private sealed class RegistryStrategy : IInt64BarStrategy, ITradeRegistryProvider, IOrderContextReceiver
    {
        public TradeRegistryModule Module { get; } = new(new TradeRegistryParams());
        private Asset _asset = null!;

        public string Version => "1.0.0";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];
        public TradeRegistryModule TradeRegistry => Module;

        public void Bind(Asset asset) => _asset = asset;
        public void SetOrderContext(IOrderContext context) => Module.SetOrderContext(context);
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) => Module.OnFill(fill, order);
        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) { }

        public void OpenEntry(long slPrice, long entryStopPrice) =>
            Module.OpenGroup(_asset, OrderSide.Buy, OrderType.Stop, 0.001m,
                slPrice, ReadOnlySpan<TpLevel>.Empty, entryStopPrice: entryStopPrice);
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

    private sealed class Fixture : IAsyncDisposable
    {
        public required LiveSessionDispatcher Dispatcher { get; init; }
        public required AccountTarget Target { get; init; }
        public required RegistryStrategy Strategy { get; init; }
        public required Asset Asset { get; init; }
        public required long ExchangeOrderId { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required OrderRouter Router { get; init; }
        public required Guid SessionId { get; init; }

        private const string QuoteAsset = "USDT";

        public async Task WaitForOrderMapped(CancellationToken ct)
        {
            for (var i = 0; i < 200 && !Router.TryResolveSession(ExchangeOrderId, out _); i++)
                await Task.Delay(10, ct);
            Assert.True(Router.TryResolveSession(ExchangeOrderId, out _),
                "entry order never re-keyed to the exchange id");
        }

        public async Task WaitForProtection(OrderGroup group, CancellationToken ct)
        {
            for (var i = 0; i < 200 && group.Status == OrderGroupStatus.PendingEntry; i++)
                await Task.Delay(10, ct);
        }

        public static async Task<Fixture> WithRegistrySession(CancellationToken ct)
        {
            var asset = CryptoAsset.Create("BTCUSDT", "Binance",
                decimalDigits: 2,
                minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

            const long exchangeOrderId = 777L;

            var client = Substitute.For<IExchangeOrderClient>();
            // Stop entry → no REST fills (empty list), so the fill arrives via the WS push path. The
            // negative module id re-keys to this exchange id on placement.
            client.PlaceOrderAsync(Arg.Any<string>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
                    Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
                .Returns(new ExchangeOrderResult(exchangeOrderId, []));

            var factory = new FixedFundsTargetFactory(client, 1_000_000_00L, QuoteAsset);
            var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);
            var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);
            var cts = new CancellationTokenSource();

            var dispatcher = new LiveSessionDispatcher(
                router, new NoopMarketDataSource(), new NoopStrategyDispatch(), reconciler,
                new LiveDispatcherOptions(1024, 4096, TimeSpan.FromSeconds(30)),
                NullLogger.Instance);
            dispatcher.Start(cts.Token);
            dispatcher.StartReconciliation();

            var sessionId = Guid.NewGuid();
            var strategy = new RegistryStrategy();
            strategy.Bind(asset);

            var config = new LiveSessionConfig
            {
                SessionId = sessionId,
                Strategy = strategy,
                AccountName = "A",
                Subscriptions = [new TimeBarSubscription(asset.Name, "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))
                    { Asset = asset }],
            };

            await dispatcher.AddSession(config, QuoteAsset, ct);

            var target = (AccountTarget)router.Targets.Single();

            return new Fixture
            {
                Dispatcher = dispatcher,
                Target = target,
                Strategy = strategy,
                Asset = asset,
                ExchangeOrderId = exchangeOrderId,
                Cts = cts,
                Router = router,
                SessionId = sessionId,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Dispatcher.Stop(Cts.Token);
            Cts.Dispose();
        }
    }
}
