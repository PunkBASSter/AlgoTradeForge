using System.Collections.Concurrent;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.Infrastructure.Live.Binance;
using AlgoTradeForge.Infrastructure.Tests.Live.Testnet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Live;

// ── Test strategy with TradeRegistry ─────────────────────────────

public sealed class TradeRegistryTestParams : StrategyParamsBase;

public sealed class TradeRegistryTestStrategy(TradeRegistryTestParams p)
    : StrategyBase<TradeRegistryTestParams>(p), ITradeRegistryProvider
{
    public override string Version => "1.0.0";

    public TradeRegistryModule TradeRegistry { get; } = new(new TradeRegistryParams());

    public ConcurrentBag<Fill> ReceivedFills { get; } = [];
    public TaskCompletionSource<Fill> NextFillTcs { get; private set; } = new();
    public TaskCompletionSource<Int64Bar> NextBarTcs { get; private set; } = new();

    public Action<IOrderContext>? OnNextBar;

    public void ResetFillTcs() => NextFillTcs = new TaskCompletionSource<Fill>();
    public void ResetBarTcs() => NextBarTcs = new TaskCompletionSource<Int64Bar>();

    public override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
    {
        NextBarTcs.TrySetResult(bar);

        var action = Interlocked.Exchange(ref OnNextBar, null);
        action?.Invoke(orders);
    }

    public override void OnTrade(Fill fill, Order order, IOrderContext orders)
    {
        ReceivedFills.Add(fill);
        TradeRegistry.OnFill(fill, order, orders);
        NextFillTcs.TrySetResult(fill);
    }
}

// ── E2E Tests ────────────────────────────────────────────────────

[Trait("Category", "Integration")]
[Trait("Requires", "BinanceTestnet")]
public sealed class BinanceLiveConnectorE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan FillTimeout = TimeSpan.FromSeconds(90);
    private const decimal MinQty = 0.00100m; // Must be > minOrderQty after TP split

    private BinanceLiveConnector? _connector;
    private TradeRegistryTestStrategy? _strategyA;
    private TradeRegistryTestStrategy? _strategyB;
    private Asset? _asset;
    private Guid _sessionIdA;
    private Guid _sessionIdB;
    private long _lastPrice;

    public async ValueTask InitializeAsync()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            return;

        var accountConfig = BinanceTestnetCredentials.CreateAccountConfig();
        var sharedOptions = new BinanceLiveOptions
        {
            ReconciliationInterval = TimeSpan.FromSeconds(5) // faster for test
        };
        var validator = new OrderValidator();
        var logger = NullLogger<BinanceLiveConnector>.Instance;

        _connector = new BinanceLiveConnector("testnet-e2e", accountConfig, sharedOptions, validator, logger);
        await _connector.ConnectAsync();

        _asset = Asset.Crypto("BTCUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.00010m, maxOrderQuantity: 9000m, quantityStepSize: 0.00010m);

        var tickerPrice = await _connector.GetTickerPriceAsync("BTCUSDT");
        _lastPrice = (long)(tickerPrice / _asset.TickSize);

        var initialCash = (long)(200m / _asset.TickSize); // 200 USDT scaled

        // Strategy A
        _strategyA = new TradeRegistryTestStrategy(new TradeRegistryTestParams());
        _sessionIdA = Guid.NewGuid();
        await _connector.AddSessionAsync(new LiveSessionConfig
        {
            SessionId = _sessionIdA,
            Strategy = _strategyA,
            Subscriptions = [new DataSubscription(_asset, TimeSpan.FromMinutes(1))],
            PrimaryAsset = _asset,
            InitialCash = initialCash,
            Routing = LiveEventRouting.All,
            AccountName = "testnet-e2e",
        });

        // Strategy B
        _strategyB = new TradeRegistryTestStrategy(new TradeRegistryTestParams());
        _sessionIdB = Guid.NewGuid();
        await _connector.AddSessionAsync(new LiveSessionConfig
        {
            SessionId = _sessionIdB,
            Strategy = _strategyB,
            Subscriptions = [new DataSubscription(_asset, TimeSpan.FromMinutes(1))],
            PrimaryAsset = _asset,
            InitialCash = initialCash,
            Routing = LiveEventRouting.All,
            AccountName = "testnet-e2e",
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_connector is null) return;

        try
        {
            if (_sessionIdA != Guid.Empty)
                await _connector.RemoveSessionAsync(_sessionIdA);
            if (_sessionIdB != Guid.Empty)
                await _connector.RemoveSessionAsync(_sessionIdB);
        }
        catch { /* best-effort cleanup */ }

        await _connector.StopAsync();
    }

    // ── Two sessions: fills route to correct session ─────────────

    [Fact(
#if DEBUG
        Skip = "Requires responsive Binance testnet — run in Release for full integration"
#endif
    )]
    public async Task TwoSessions_FillsRouteToCorrectSession()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        // Strategy A: market buy
        _strategyA!.ResetFillTcs();
        _strategyA.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = _asset!,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = MinQty,
            });
        };

        var fillA = await _strategyA.NextFillTcs.Task.WaitAsync(FillTimeout);
        Assert.Equal(OrderSide.Buy, fillA.Side);

        // Strategy B: market buy
        _strategyB!.ResetFillTcs();
        _strategyB.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = _asset!,
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = MinQty,
            });
        };

        var fillB = await _strategyB.NextFillTcs.Task.WaitAsync(FillTimeout);
        Assert.Equal(OrderSide.Buy, fillB.Side);

        // Each strategy only received its own fill
        Assert.Single(_strategyA.ReceivedFills);
        Assert.Single(_strategyB.ReceivedFills);
    }

    // ── Two sessions: reconciliation sees expected orders ─────────

    [Fact(
#if DEBUG
        Skip = "Requires responsive Binance testnet — run in Release for full integration"
#endif
    )]
    public async Task TwoSessions_ReconciliationSeesExpectedOrders()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        // Strategy A: open group via TradeRegistry (market entry)
        var tpPriceA = _lastPrice + (long)(500m / _asset!.TickSize);
        var slPriceA = _lastPrice - (long)(500m / _asset!.TickSize);

        _strategyA!.ResetFillTcs();
        _strategyA.OnNextBar = orders =>
        {
            _strategyA.TradeRegistry.OpenGroup(
                orders, _asset!, OrderSide.Buy, OrderType.Market,
                quantity: MinQty, slPrice: slPriceA,
                tpLevels: [new TpLevel { Price = tpPriceA, ClosurePercentage = 1.0m }]);
        };

        // Wait for entry fill
        await _strategyA.NextFillTcs.Task.WaitAsync(FillTimeout);

        // Strategy B: open group via TradeRegistry (market entry)
        var tpPriceB = _lastPrice + (long)(600m / _asset!.TickSize);
        var slPriceB = _lastPrice - (long)(600m / _asset!.TickSize);

        _strategyB!.ResetFillTcs();
        _strategyB.OnNextBar = orders =>
        {
            _strategyB.TradeRegistry.OpenGroup(
                orders, _asset!, OrderSide.Buy, OrderType.Market,
                quantity: MinQty, slPrice: slPriceB,
                tpLevels: [new TpLevel { Price = tpPriceB, ClosurePercentage = 1.0m }]);
        };

        await _strategyB.NextFillTcs.Task.WaitAsync(FillTimeout);

        // Allow protective orders to be placed on exchange
        await Task.Delay(3000);

        // Each TradeRegistry should have expected orders (1 SL + 1 TP)
        var expectedA = _strategyA.TradeRegistry.GetExpectedOrders();
        var expectedB = _strategyB.TradeRegistry.GetExpectedOrders();

        Assert.Equal(2, expectedA.Count); // SL + TP
        Assert.Equal(2, expectedB.Count); // SL + TP

        // Verify isolation — different group IDs
        var groupIdsA = expectedA.Select(e => e.GroupId).Distinct().ToList();
        var groupIdsB = expectedB.Select(e => e.GroupId).Distinct().ToList();
        Assert.Single(groupIdsA);
        Assert.Single(groupIdsB);
    }

    // ── Shutdown: cancels all open orders ─────────────────────────

    [Fact(
#if DEBUG
        Skip = "Requires responsive Binance testnet — run in Release for full integration"
#endif
    )]
    public async Task Shutdown_CancelsAllOpenOrders()
    {
        if (!BinanceTestnetCredentials.IsConfigured)
            Assert.Skip(BinanceTestnetCredentials.SkipReason);

        // Place a limit order far from market (shouldn't fill)
        var farLimitPrice = _lastPrice / 2;

        _strategyA!.ResetBarTcs();
        _strategyA.OnNextBar = orders =>
        {
            orders.Submit(new Order
            {
                Id = 0,
                Asset = _asset!,
                Side = OrderSide.Buy,
                Type = OrderType.Limit,
                Quantity = MinQty,
                LimitPrice = farLimitPrice,
            });
        };

        await _strategyA.NextBarTcs.Task.WaitAsync(FillTimeout);
        await Task.Delay(2000); // Let order reach exchange

        // Stop connector — should trigger safety-net cancel-all
        await _connector!.StopAsync();

        // Create a fresh API client to verify no open orders remain
        var accountConfig = BinanceTestnetCredentials.CreateAccountConfig();
        var apiClient = new BinanceApiClient(
            accountConfig.RestUrl, accountConfig.ApiKey, accountConfig.ApiSecret,
            NullLogger<BinanceLiveConnector>.Instance);

        try
        {
            await apiClient.SyncTimeAsync();
            var openOrders = await apiClient.GetOpenOrdersAsync("BTCUSDT");
            Assert.Empty(openOrders);
        }
        finally
        {
            apiClient.Dispose();
        }

        // Prevent DisposeAsync from trying to stop again
        _connector = null;
    }
}
