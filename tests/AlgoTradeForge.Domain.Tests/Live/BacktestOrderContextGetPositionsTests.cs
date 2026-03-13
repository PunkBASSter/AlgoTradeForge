using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Live;

public class BacktestOrderContextGetPositionsTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    [Fact]
    public void GetPositions_ReturnsPortfolioPositions()
    {
        var fills = new List<Fill>();
        var queue = new OrderQueue();
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();

        var ctx = new BacktestOrderContext(queue, fills, portfolio, NullEventBus.Instance, new OrderValidator(), []);

        // Before any trades, positions should be empty
        var positions = ctx.GetPositions();
        Assert.Empty(positions);

        // Simulate a fill (adds position)
        var fill = new Fill(1, TestAssets.Aapl, Start, 10000, 5m, OrderSide.Buy, 0);
        portfolio.Apply(fill);

        positions = ctx.GetPositions();
        Assert.Single(positions);
        Assert.True(positions.ContainsKey("AAPL"));
        Assert.Equal(5m, positions["AAPL"].Quantity);
    }

    [Fact]
    public void OnTrade_ReceivesOrderContext()
    {
        var sub = new DataSubscription(TestAssets.Aapl, OneMinute);
        IOrderContext? receivedContext = null;

        var strategy = new OnTradeCapturingStrategy(sub)
        {
            OnTradeAction = (_, _, ctx) => receivedContext = ctx,
            OnBarCompleteAction = (_, _, orders) =>
            {
                if (receivedContext is not null) return;
                orders.Submit(new Order
                {
                    Id = 0,
                    Asset = TestAssets.Aapl,
                    Side = OrderSide.Buy,
                    Type = OrderType.Market,
                    Quantity = 1m,
                });
            }
        };

        var engine = new BacktestEngine(new BarMatcher(), new OrderValidator());
        var bars = TestBars.CreateSeries(Start, OneMinute, 2, startPrice: 1000);
        var options = new BacktestOptions
        {
            InitialCash = 100_000L,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        engine.Run([bars], strategy, options);

        Assert.NotNull(receivedContext);
        Assert.True(receivedContext.Cash > 0);
    }

    private sealed class OnTradeCapturingStrategy(DataSubscription subscription) : IInt64BarStrategy
    {
        public string Version => "1.0.0";
        public IList<DataSubscription> DataSubscriptions { get; } = [subscription];

        public Action<Fill, Order, IOrderContext>? OnTradeAction { get; init; }
        public Action<Int64Bar, DataSubscription, IOrderContext>? OnBarCompleteAction { get; init; }

        public void OnInit() { }
        public void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders) =>
            OnBarCompleteAction?.Invoke(bar, subscription, orders);
        public void OnTrade(Fill fill, Order order, IOrderContext orders) =>
            OnTradeAction?.Invoke(fill, order, orders);
    }
}
