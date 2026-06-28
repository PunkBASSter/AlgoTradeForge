using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class LiveOrderContextTests
{
    private static readonly CryptoAsset BtcUsdt = CryptoAsset.Create("BTCUSDT", "Binance",
        decimalDigits: 2,
        minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    private static LiveOrderContext CreateContext()
    {
        var portfolio = new Portfolio { InitialCash = 100_000_00L }; // 100,000 at 0.01 tick
        portfolio.Initialize();

        // Use a throwaway API client pointing to testnet (won't be called in these tests)
        var apiClient = new BinanceApiClient(
            "https://testnet.binance.vision", "fake", "fake", NullLogger.Instance);

        return new LiveOrderContext(
            portfolio, new OrderValidator(), NullLogger.Instance, apiClient);
    }

    [Fact]
    public void Cancel_NonExistentOrder_ReturnsNull()
    {
        var ctx = CreateContext();
        Assert.Null(ctx.Cancel(999));
    }

    [Fact]
    public void GetPositions_Empty_Initially()
    {
        var ctx = CreateContext();
        Assert.Empty(ctx.GetPositions());
    }

    [Fact]
    public void GetPositions_ReturnsPortfolioPositions_AfterFill()
    {
        var ctx = CreateContext();

        var fill = new Fill(1, BtcUsdt, DateTimeOffset.UtcNow, 5000000L, 0.001m, OrderSide.Buy, 0);
        ctx.AddFill(fill);

        var positions = ctx.GetPositions();
        Assert.Single(positions);
        Assert.True(positions.ContainsKey("BTCUSDT"));
        Assert.Equal(0.001m, positions["BTCUSDT"].Quantity);
    }

    [Fact]
    public void GetFills_ReturnsFillsAddedSinceLastClear()
    {
        var ctx = CreateContext();

        var fill1 = new Fill(1, BtcUsdt, DateTimeOffset.UtcNow, 5000000L, 0.001m, OrderSide.Buy, 0);
        ctx.AddFill(fill1);
        Assert.Single(ctx.GetFills());

        ctx.ClearRecentFills();
        Assert.Empty(ctx.GetFills());

        var fill2 = new Fill(2, BtcUsdt, DateTimeOffset.UtcNow, 5100000L, 0.001m, OrderSide.Sell, 0);
        ctx.AddFill(fill2);
        Assert.Single(ctx.GetFills());
    }

    [Fact]
    public void Cash_ReflectsPortfolioCash()
    {
        var ctx = CreateContext();
        Assert.Equal(100_000_00L, ctx.Cash);
    }

    [Fact]
    public void Cash_UpdatesAfterFill()
    {
        var ctx = CreateContext();
        var initialCash = ctx.Cash;

        var fill = new Fill(1, BtcUsdt, DateTimeOffset.UtcNow, 5000000L, 0.001m, OrderSide.Buy, 100);
        ctx.AddFill(fill);

        Assert.True(ctx.Cash < initialCash);
    }

    [Fact]
    public void Submit_InvalidOrder_GetsRejected()
    {
        var ctx = CreateContext();

        var order = new Order
        {
            Id = 0,
            Asset = BtcUsdt,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = -1m,
        };

        ctx.Submit(order, Guid.NewGuid());

        Assert.Equal(OrderStatus.Rejected, order.Status);
    }

    [Fact]
    public void GetPendingOrders_EmptyByDefault()
    {
        var ctx = CreateContext();
        Assert.Empty(ctx.GetPendingOrders());
    }

    [Fact]
    public void Submit_ValidOrder_SetsPendingStatus()
    {
        var ctx = CreateContext();
        ctx.Start(CancellationToken.None);

        var order = new Order
        {
            Id = 0,
            Asset = BtcUsdt,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = 0.001m,
            LimitPrice = 5000000L,
        };

        var id = ctx.Submit(order, Guid.NewGuid());

        Assert.True(id > 0);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Single(ctx.GetPendingOrders());
    }

    [Fact]
    public void AddFill_UpdatesPortfolioInsideLock()
    {
        // Issue 5: Verify AddFill is self-contained (portfolio updated inside lock)
        var ctx = CreateContext();
        var initialCash = ctx.Cash;

        var fill = new Fill(1, BtcUsdt, DateTimeOffset.UtcNow, 5000000L, 0.001m, OrderSide.Buy, 0);
        ctx.AddFill(fill);

        // Cash should be updated atomically with the fill
        Assert.True(ctx.Cash < initialCash);
        Assert.Single(ctx.GetFills());
    }

    [Fact]
    public void Cancel_ByLocalId_AfterRekeying_FindsOrderByBinanceId()
    {
        var ctx = CreateContext();
        ctx.Start(CancellationToken.None);

        var order = new Order
        {
            Id = 0,
            Asset = BtcUsdt,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = 0.001m,
            LimitPrice = 5000000L,
        };

        var localId = ctx.Submit(order, Guid.NewGuid());

        // Simulate Binance order placement response rekeying
        const long binanceOrderId = 9999999L;
        ctx.RekeyToExchangeId(localId, binanceOrderId);

        // The order should now be keyed by Binance ID, not local ID
        Assert.Null(ctx.GetPendingOrder(localId));
        Assert.NotNull(ctx.GetPendingOrder(binanceOrderId));

        // Cancel using original local ID — should resolve to Binance ID
        var cancelled = ctx.Cancel(localId);

        Assert.NotNull(cancelled);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Empty(ctx.GetPendingOrders());
    }

    [Fact]
    public void Cancel_ByLocalId_BeforePlacement_StillWorks()
    {
        var ctx = CreateContext();
        ctx.Start(CancellationToken.None);

        var order = new Order
        {
            Id = 0,
            Asset = BtcUsdt,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = 0.001m,
            LimitPrice = 5000000L,
        };

        var localId = ctx.Submit(order, Guid.NewGuid());

        // Cancel immediately before any placement (no rekeying happened)
        var cancelled = ctx.Cancel(localId);

        Assert.NotNull(cancelled);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Empty(ctx.GetPendingOrders());
    }

    [Fact]
    public void Submit_AfterRekeyToOverlappingExchangeId_TracksBothOrdersIndependently()
    {
        // IB exchange ids are small ints that overlap the auto-assigned local-id range. Reproduce the
        // collision: order A re-keys to a small exchange id equal to the NEXT order's local id. The two
        // keyspaces must stay disjoint so B is not dropped and A is not clobbered.
        var ctx = CreateContext();

        var a = NewLimit();
        var localA = ctx.Submit(a, Guid.NewGuid());

        var collidingExchangeId = localA + 1; // the id Submit will hand the next plain order
        ctx.RekeyToExchangeId(localA, collidingExchangeId);

        var b = NewLimit();
        var localB = ctx.Submit(b, Guid.NewGuid());

        Assert.Equal(collidingExchangeId, localB);          // B's local id overlaps A's exchange id
        Assert.Equal(2, ctx.GetPendingOrders().Count);      // both tracked — pre-fix B was silently dropped

        Assert.Same(a, ctx.GetPendingOrder(collidingExchangeId)); // exchange space resolves to A

        const long exchangeB = 9_000_000L;
        ctx.RekeyToExchangeId(localB, exchangeB);

        Assert.Same(b, ctx.GetPendingOrder(exchangeB));
        Assert.Same(a, ctx.GetPendingOrder(collidingExchangeId)); // A untouched by B's placement
        Assert.Equal(2, ctx.GetPendingOrders().Count);
    }

    private static Order NewLimit() => new()
    {
        Id = 0,
        Asset = BtcUsdt,
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        Quantity = 0.001m,
        LimitPrice = 5000000L,
    };

    [Fact]
    public void OrderMapped_CarriesOriginatingSessionId_AfterRekey()
    {
        var portfolio = new Portfolio { InitialCash = 100_000_00L };
        portfolio.Initialize();
        var ctx = new LiveOrderContext(
            portfolio, new OrderValidator(), NullLogger.Instance,
            Substitute.For<IExchangeOrderClient>());
        ctx.Start(CancellationToken.None);

        var sessionId = Guid.NewGuid();
        (long mappedExchangeId, Guid mappedSession)? captured = null;
        ctx.OrderMapped += (exId, sId) => captured = (exId, sId);

        var order = new Order { Id = 0, Asset = BtcUsdt, Side = OrderSide.Buy,
            Type = OrderType.Limit, Quantity = 0.001m, LimitPrice = 5000000L };
        var localId = ctx.Submit(order, sessionId);

        const long exchangeId = 4242L;
        ctx.RekeyToExchangeId(localId, exchangeId);

        Assert.Equal((exchangeId, sessionId), captured);
    }

    [Fact]
    public void AddFill_UpdatesCashAndPositions()
    {
        var ctx = CreateContext();
        var initialCash = ctx.Cash;

        var fill = new Fill(1, BtcUsdt, DateTimeOffset.UtcNow, 5000000L, 0.001m, OrderSide.Buy, 100);
        ctx.AddFill(fill);

        // Cash should decrease (cost of fill + commission)
        Assert.True(ctx.Cash < initialCash);

        // Position should exist
        var positions = ctx.GetPositions();
        Assert.Single(positions);
        Assert.Equal(0.001m, positions["BTCUSDT"].Quantity);
    }

    [Fact]
    public async Task ProcessOrders_ScalesPrice_OffOrderAsset_NotAConstant()
    {
        // EthUsdt has a coarser tick (0.01) than a hypothetical 8-dp asset; the order's OWN
        // asset must drive scaling. Build the context, submit a LIMIT order, capture the price.
        var ethUsdt = CryptoAsset.Create("ETHUSDT", "Binance",
            decimalDigits: 2, minOrderQuantity: 0.0001m, maxOrderQuantity: 9000m, quantityStepSize: 0.0001m);

        var client = Substitute.For<IExchangeOrderClient>();
        client.PlaceOrderAsync(Arg.Any<string>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
                Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(new ExchangeOrderResult(7777L, []));

        var portfolio = new Portfolio { InitialCash = 100_000_00L };
        portfolio.Initialize();
        var ctx = new LiveOrderContext(
            portfolio, new OrderValidator(), NullLogger.Instance, client);
        ctx.Start(TestContext.Current.CancellationToken);

        // LimitPrice is tick-denominated: 3000.00 ETH at 0.01 tick = 300000 ticks.
        var order = new Order { Id = 0, Asset = ethUsdt, Side = OrderSide.Buy,
            Type = OrderType.Limit, Quantity = 0.01m, LimitPrice = 300000L };
        ctx.Submit(order, Guid.NewGuid());

        // Poll until the single-reader order task drains the channel.
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 50 && client.ReceivedCalls().Count() == 0; i++)
            await Task.Delay(20, ct);

        await client.Received().PlaceOrderAsync("ETHUSDT", OrderSide.Buy, OrderType.Limit,
            0.01m, 3000.00m, null, Arg.Any<CancellationToken>());
    }
}
