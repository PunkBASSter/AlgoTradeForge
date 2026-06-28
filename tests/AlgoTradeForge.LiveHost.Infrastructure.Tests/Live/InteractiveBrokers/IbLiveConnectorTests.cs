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
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

// Composition-root wiring test for IbLiveConnector. The shared IB socket is faked via the order-gateway
// seam (FakeIbGateway) + a fake market-data session; the data plane is no-op'd. The test exercises the
// observable wiring: ConnectAsync -> Running, AddSession -> a bound target + strategy order context, and a
// synthetic execDetails (driven through the fake gateway's onReport) -> dispatcher -> session -> OnTrade.
public sealed class IbLiveConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EquityAsset Aapl = new() { Name = "AAPL", Exchange = "NASDAQ" };

    private static readonly ResolvedIbContract AaplResolved = new(
        new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD"),
        ConId: 265598, LocalSymbol: "AAPL", LastTradeDate: "");

    [Fact]
    public async Task ConnectAsync_TransitionsToRunning_AndConnectsSession()
    {
        await using var h = await Harness.ConnectedAsync(Ct);

        Assert.Equal(LiveSessionStatus.Running, h.Connector.Status);
        Assert.Equal(1, h.Session.ConnectCount);
    }

    [Fact]
    public async Task AddSessionAsync_ResolvesTarget_AndBindsStrategyOrderContext()
    {
        await using var h = await Harness.ConnectedAsync(Ct);

        await h.Connector.AddSessionAsync(h.SessionConfig, Ct);

        Assert.Equal(1, h.Connector.SessionCount);
        // The strategy received an order context (so it can submit) — proven by a successful submit below.
        Assert.NotNull(h.Strategy.OrderContext);
    }

    [Fact]
    public async Task ExecDetails_ForPlacedOrder_DrivesStrategyOnTrade()
    {
        await using var h = await Harness.ConnectedAsync(Ct);
        await h.Connector.AddSessionAsync(h.SessionConfig, Ct);

        // Place an order through the strategy's bound context. IbExchangeOrderClient -> FakeIbGateway.Place
        // returns an exchange id; LiveOrderContext re-keys to it and fires OrderMapped -> router.TrackOrder.
        h.Strategy.OrderContext!.Submit(new Order
        {
            Id = 0, Asset = Aapl, Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 5m,
        });

        await Poll(() => h.Gateway.LastPlacedOrderId > 0);
        var orderId = h.Gateway.LastPlacedOrderId;

        // A synthetic fill arrives as an ExecutionReport (what the real gateway emits via onReport).
        h.Gateway.EmitTrade(orderId, Aapl, OrderSide.Buy, price: 195.50m, qty: 5m);

        await Poll(() => h.Strategy.ReceivedTrades.Count > 0);

        var fill = Assert.Single(h.Strategy.ReceivedTrades);
        Assert.Equal(orderId, fill.OrderId);
        Assert.Equal(OrderSide.Buy, fill.Side);
    }

    private static async Task Poll(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++)
            await Task.Delay(10);
    }

    // -----------------------------------------------------------------------
    // Harness + fakes
    // -----------------------------------------------------------------------

    private sealed class Harness : IAsyncDisposable
    {
        public required IbLiveConnector Connector { get; init; }
        public required FakeIbGateway Gateway { get; init; }
        public required FakeReconnectingSession Session { get; init; }
        public required RecordingStrategy Strategy { get; init; }
        public required LiveSessionConfig SessionConfig { get; init; }

        public static async Task<Harness> ConnectedAsync(CancellationToken ct)
        {
            var session = new FakeReconnectingSession();
            var gateway = new FakeIbGateway();

            var resolver = Substitute.For<IIbContractResolver>();
            resolver.Resolve(Arg.Any<IbContract>(), Arg.Any<CancellationToken>()).Returns(AaplResolved);

            var summary = new FakeIbAccountSummaryClient(
                [new IbAccountSummaryRow("DU1", "AvailableFunds", "100000.00", "USD")]);

            var strategy = new RecordingStrategy();
            var config = new LiveSessionConfig
            {
                SessionId = Guid.NewGuid(),
                Strategy = strategy,
                AccountName = "DU1",
                Subscriptions =
                [
                    new TimeBarSubscription("AAPL", "NASDAQ", DataFeedRole.Primary, TimeFrame.Parse("1h"))
                    { Asset = Aapl },
                ],
            };

            var connector = new IbLiveConnector(
                accountName: "ib",
                session: session,
                contractResolver: resolver,
                summaryClient: summary,
                orderValidator: new OrderValidator(),
                tickRouter: new NoopMarketDataSource(),
                dispatch: new NoopStrategyDispatch(),
                options: new LiveDispatcherOptions(1024, 4096, TimeSpan.FromSeconds(30)),
                loggerFactory: NullLoggerFactory.Instance,
                gatewayFactory: onReport => gateway.Bind(onReport));

            await connector.ConnectAsync(ct);

            return new Harness
            {
                Connector = connector, Gateway = gateway, Session = session,
                Strategy = strategy, SessionConfig = config,
            };
        }

        public ValueTask DisposeAsync() => Connector.DisposeAsync();
    }

    // Captures the gateway's onReport callback (the connector wires it to dispatcher.OnExecutionReport) and
    // records placements; EmitTrade fires a neutral ExecutionReport exactly as the real gateway does off-pump.
    private sealed class FakeIbGateway : IIbOrderGateway
    {
        private Action<ExecutionReport>? _onReport;
        private long _nextId = 1000L;

        public long LastPlacedOrderId { get; private set; }

        public FakeIbGateway Bind(Action<ExecutionReport> onReport)
        {
            _onReport = onReport;
            return this;
        }

        public Task<long> Place(string account, Asset asset, ResolvedIbContract contract, IbOrderRequest request,
            OrderSide side, OrderType type, decimal originalQuantity, CancellationToken ct = default)
        {
            var id = Interlocked.Increment(ref _nextId);
            LastPlacedOrderId = id;
            return Task.FromResult(id);
        }

        public void Cancel(long orderId) { }

        public void EmitTrade(long orderId, Asset asset, OrderSide side, decimal price, decimal qty) =>
            _onReport!(new ExecutionReport(
                OrderId: orderId, Asset: asset, Side: side, ExecType: ExecType.Trade,
                LastFillPrice: price, LastFillQty: qty, Commission: 0m, Status: OrderStatus.Filled,
                TransactionTime: DateTimeOffset.UnixEpoch, Type: OrderType.Market, OriginalQuantity: qty));
    }

    private sealed class FakeReconnectingSession : IIbMarketDataSession
    {
        public int ConnectCount { get; private set; }
        public event Action? Reconnected { add { } remove { } }

        public Task Connect(CancellationToken ct = default)
        {
            ConnectCount++;
            return Task.CompletedTask;
        }

        public int SubscribeTrades(ResolvedIbContract contract, Action<IbTradeUpdate> sink) => 1;
        public int SubscribeRealtimeBars(ResolvedIbContract contract, Action<IbRealtimeBar> sink) => 1;
        public void Unsubscribe(int reqId) { }
    }

    private sealed class RecordingStrategy : IInt64BarStrategy, IOrderContextReceiver
    {
        public List<Fill> ReceivedTrades { get; } = [];
        public IOrderContext? OrderContext { get; private set; }
        public string Version => "1.0.0";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];
        public void OnInit() { }
        public void SetOrderContext(IOrderContext context) => OrderContext = context;
        public void OnTrade(Fill fill, Order order) => ReceivedTrades.Add(fill);
        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) { }
    }

    private sealed class NoopMarketDataSource : ITickRouter
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
