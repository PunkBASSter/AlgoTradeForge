using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public sealed class LiveSessionDispatcherTests
{
    [Fact]
    public async Task OnExecutionReport_RoutesFillToOriginatingSession_AndAppliesToSharedPortfolio()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = await DispatcherFixture.WithOneSession(ct);

        fixture.Dispatcher.OnExecutionReport(new ExecutionReport(
            OrderId: fixture.ExchangeOrderId,
            Asset: fixture.Asset,
            Side: OrderSide.Buy,
            ExecType: ExecType.Trade,
            LastFillPrice: 100m,
            LastFillQty: 1m,
            Commission: 0m,
            Status: OrderStatus.Filled));

        await fixture.DrainEventQueue(ct);

        Assert.Single(fixture.Strategy.ReceivedTrades);
        Assert.Equal(1m, fixture.Target.Portfolio.Positions[fixture.Asset.Name].Quantity);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task OnExecutionReport_BuffersUnmappedOrder_ReplaysOnTrack()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = await DispatcherFixture.WithOneSession(ct);

        // The exchange will assign this id on placement; until then the report is unmapped.
        const long unmappedOrderId = 999L;
        fixture.Client.PlaceOrderAsync(Arg.Any<string>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
                Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(new ExchangeOrderResult(unmappedOrderId, []));

        var report = new ExecutionReport(unmappedOrderId, fixture.Asset, OrderSide.Buy, ExecType.Trade,
            100m, 1m, 0m, OrderStatus.Filled);

        fixture.Dispatcher.OnExecutionReport(report); // unmapped → buffered, no throw

        // Mapping arrives later: submit an order that re-keys to unmappedOrderId. The account context
        // fires OrderMapped, which the dispatcher turns into TrackOrder + DrainBufferedReports.
        var submittedOrder = new Order
        {
            Id = 0, Asset = fixture.Asset, Side = OrderSide.Buy, Type = OrderType.Limit,
            Quantity = 1m, LimitPrice = 5_000_000L,
        };
        fixture.SessionContext.Submit(submittedOrder);

        // The order places + re-keys to unmappedOrderId, firing OrderMapped → TrackOrder +
        // DrainBufferedReports. The replayed (Filled) trade then fires OnTrade. We assert on the
        // strategy's received trade — the order→session map is intentionally untracked once Filled.
        await fixture.DrainEventQueue(ct);

        Assert.Single(fixture.Strategy.ReceivedTrades);
        Assert.Equal(unmappedOrderId, fixture.Strategy.ReceivedTrades[0].OrderId);

        await fixture.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Fixture + fakes (mirrors MultiAccountRoutingTests; uses a real AccountTarget
    // because LiveSessionDispatcher narrows ResolveTarget to the concrete AccountTarget).
    // -----------------------------------------------------------------------

    private sealed class RecordingStrategy : IInt64BarStrategy
    {
        public List<Fill> ReceivedTrades { get; } = [];
        public string Version => "1.0.0";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) => ReceivedTrades.Add(fill);
        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) { }
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

    private sealed class DispatcherFixture : IAsyncDisposable
    {
        public required LiveSessionDispatcher Dispatcher { get; init; }
        public required AccountTarget Target { get; init; }
        public required RecordingStrategy Strategy { get; init; }
        public required Asset Asset { get; init; }
        public required Guid SessionId { get; init; }
        public required long ExchangeOrderId { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required IExchangeOrderClient Client { get; init; }
        public required OrderRouter Router { get; init; }
        public required IOrderContext SessionContext { get; init; }

        private const string QuoteAsset = "USDT";

        public async Task DrainEventQueue(CancellationToken ct)
        {
            // The dispatcher writes a single callback per report; poll until OnTrade has fired
            // (the single-reader ProcessingTask drains it). Mirrors MultiAccountRoutingTests.Poll.
            for (var i = 0; i < 100 && Strategy.ReceivedTrades.Count == 0; i++)
                await Task.Delay(10, ct);
        }

        public static async Task<DispatcherFixture> WithOneSession(CancellationToken ct)
        {
            var asset = CryptoAsset.Create("BTCUSDT", "Binance",
                decimalDigits: 2,
                minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

            var client = Substitute.For<IExchangeOrderClient>();
            var factory = new FixedFundsTargetFactory(client, 1_000_000_00L, QuoteAsset);
            var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);
            var source = new NoopMarketDataSource();
            var reconciler = new OrderGroupReconciler(client, NullLogger.Instance);
            var cts = new CancellationTokenSource();

            var dispatcher = new LiveSessionDispatcher(
                router, source, new NoopStrategyDispatch(), reconciler,
                new LiveDispatcherOptions(1024, 4096, TimeSpan.FromSeconds(30)),
                NullLogger.Instance);
            dispatcher.Start(cts.Token);

            var sessionId = Guid.NewGuid();
            var strategy = new RecordingStrategy();
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

            const long exchangeOrderId = 555L;
            // Seed the order→session map so a trade report for this id routes (mirrors a placed order
            // that already re-keyed to an exchange id).
            router.TrackOrder(exchangeOrderId, sessionId);

            return new DispatcherFixture
            {
                Dispatcher = dispatcher,
                Target = target,
                Strategy = strategy,
                Asset = asset,
                SessionId = sessionId,
                ExchangeOrderId = exchangeOrderId,
                Cts = cts,
                Client = client,
                Router = router,
                SessionContext = target.OrderContextFor(sessionId),
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Dispatcher.Stop(Cts.Token);
            Cts.Dispose();
        }
    }
}
