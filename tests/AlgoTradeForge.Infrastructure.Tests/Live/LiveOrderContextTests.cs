using System.Collections.Concurrent;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.Infrastructure.Live;
using AlgoTradeForge.Infrastructure.Live.Binance;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Live;

public class LiveOrderContextTests
{
    private static readonly Asset BtcUsdt = Asset.Crypto("BTCUSDT", "Binance",
        decimalDigits: 2, historyStart: new DateOnly(2024, 1, 1),
        minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    private static LiveOrderContext CreateContext()
    {
        var portfolio = new Portfolio { InitialCash = 100_000_00L }; // 100,000 at 0.01 tick
        portfolio.Initialize();

        // Use a throwaway API client pointing to testnet (won't be called in these tests)
        var apiClient = new BinanceApiClient(
            "https://testnet.binance.vision", "fake", "fake", NullLogger.Instance);

        return new LiveOrderContext(
            portfolio, BtcUsdt, new OrderValidator(),
            NullLogger.Instance, apiClient,
            Guid.NewGuid(), new ConcurrentDictionary<long, Guid>());
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

        ctx.Submit(order);

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

        var id = ctx.Submit(order);

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

        var localId = ctx.Submit(order);

        // Simulate Binance order placement response rekeying
        const long binanceOrderId = 9999999L;
        ctx.SimulateOrderPlaced(localId, binanceOrderId);

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

        var localId = ctx.Submit(order);

        // Cancel immediately before any placement (no rekeying happened)
        var cancelled = ctx.Cancel(localId);

        Assert.NotNull(cancelled);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Empty(ctx.GetPendingOrders());
    }

    [Fact]
    public void OrderMapped_FiredAfterSimulateOrderPlaced()
    {
        var ctx = CreateContext();
        ctx.Start(CancellationToken.None);

        long? mappedOrderId = null;
        ctx.OrderMapped += id => mappedOrderId = id;

        var order = new Order
        {
            Id = 0,
            Asset = BtcUsdt,
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = 0.001m,
            LimitPrice = 5000000L,
        };

        var localId = ctx.Submit(order);
        const long binanceOrderId = 12345L;
        ctx.SimulateOrderPlaced(localId, binanceOrderId);

        Assert.Equal(binanceOrderId, mappedOrderId);
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
}
