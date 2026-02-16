using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class OrderQueueTests
{
    private readonly OrderQueue _queue = new();

    [Fact]
    public void Submit_AddsOrderToQueue()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);

        _queue.Submit(order);

        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public void Submit_MultipleOrders_PreservesFifoOrder()
    {
        var order1 = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var order2 = TestOrders.LimitBuy(TestAssets.Aapl, 50m, 150L);
        var order3 = TestOrders.StopBuy(TestAssets.Aapl, 25m, 160L);

        _queue.Submit(order1);
        _queue.Submit(order2);
        _queue.Submit(order3);

        var all = _queue.GetAll();
        Assert.Equal(3, all.Count);
        Assert.Equal(order1.Id, all[0].Id);
        Assert.Equal(order2.Id, all[1].Id);
        Assert.Equal(order3.Id, all[2].Id);
    }

    [Fact]
    public void Cancel_PendingOrder_ReturnsTrue()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        _queue.Submit(order);

        var result = _queue.Cancel(order.Id);

        Assert.True(result);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void Cancel_FilledOrder_ReturnsFalse()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        order.Status = OrderStatus.Filled;
        _queue.Submit(order);

        var result = _queue.Cancel(order.Id);

        Assert.False(result);
    }

    [Fact]
    public void Cancel_NonExistentOrder_ReturnsFalse()
    {
        var result = _queue.Cancel(999);

        Assert.False(result);
    }

    [Fact]
    public void GetPendingForAsset_FiltersCorrectly()
    {
        var aaplBuy = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var btcBuy = TestOrders.MarketBuy(TestAssets.BtcUsdt, 1m);
        var aaplSell = TestOrders.LimitSell(TestAssets.Aapl, 50m, 160L);

        _queue.Submit(aaplBuy);
        _queue.Submit(btcBuy);
        _queue.Submit(aaplSell);

        var aaplPending = _queue.GetPendingForAsset(TestAssets.Aapl);

        Assert.Equal(2, aaplPending.Count);
        Assert.All(aaplPending, o => Assert.Equal(TestAssets.Aapl, o.Asset));
    }

    [Fact]
    public void GetPendingForAsset_ExcludesFilledOrders()
    {
        var order1 = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var order2 = TestOrders.MarketBuy(TestAssets.Aapl, 50m);
        order2.Status = OrderStatus.Filled;

        _queue.Submit(order1);
        _queue.Submit(order2);

        var pending = _queue.GetPendingForAsset(TestAssets.Aapl);

        Assert.Single(pending);
        Assert.Equal(order1.Id, pending[0].Id);
    }

    [Fact]
    public void GetPendingForAsset_IncludesTriggeredOrders()
    {
        var order = TestOrders.StopBuy(TestAssets.Aapl, 100m, 160L);
        order.Status = OrderStatus.Triggered;
        _queue.Submit(order);

        var pending = _queue.GetPendingForAsset(TestAssets.Aapl);

        Assert.Single(pending);
    }

    [Fact]
    public void Remove_RemovesOrderById()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        _queue.Submit(order);

        _queue.Remove(order.Id);

        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public void GtcPersistence_PendingOrderSurvivesManyTicks()
    {
        var order = TestOrders.LimitBuy(TestAssets.Aapl, 100m, 14000L);
        _queue.Submit(order);

        // Simulate 100+ bar ticks — order should persist as GTC
        for (var i = 0; i < 150; i++)
        {
            var pending = _queue.GetPendingForAsset(TestAssets.Aapl);
            Assert.Single(pending);
            Assert.Equal(order.Id, pending[0].Id);
        }

        Assert.Equal(OrderStatus.Pending, order.Status);
    }
}
