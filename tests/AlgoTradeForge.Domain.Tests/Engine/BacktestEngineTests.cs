using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class BacktestEngineTests
{
    private readonly IBarMatcher _barMatcher;
    private readonly IMetricsCalculator _metricsCalculator;
    private readonly BacktestEngine _engine;

    public BacktestEngineTests()
    {
        _barMatcher = Substitute.For<IBarMatcher>();
        _metricsCalculator = Substitute.For<IMetricsCalculator>();
        _metricsCalculator.Calculate(
            Arg.Any<IReadOnlyList<Fill>>(),
            Arg.Any<IReadOnlyList<OhlcvBar>>(),
            Arg.Any<Portfolio>(),
            Arg.Any<decimal>(),
            Arg.Any<Asset>())
            .Returns(CreateEmptyMetrics());
        _engine = new BacktestEngine(_barMatcher, _metricsCalculator);
    }

    private static PerformanceMetrics CreateEmptyMetrics() =>
        new()
        {
            TotalTrades = 0,
            WinningTrades = 0,
            LosingTrades = 0,
            NetProfit = 0m,
            GrossProfit = 0m,
            GrossLoss = 0m,
            TotalReturnPct = 0,
            AnnualizedReturnPct = 0,
            SharpeRatio = 0,
            SortinoRatio = 0,
            MaxDrawdownPct = 0,
            WinRatePct = 0,
            ProfitFactor = 0,
            AverageWin = 0,
            AverageLoss = 0,
            InitialCapital = 100_000m,
            FinalEquity = 100_000m,
            TradingDays = 0
        };

    private BacktestOptions CreateOptions() =>
        new()
        {
            InitialCash = 100_000m,
            Asset = TestAssets.Aapl,
            CommissionPerTrade = 0m
        };

    private static IBarSource CreateBarSource(params OhlcvBar[] bars)
    {
        var source = Substitute.For<IBarSource>();
        source.GetBarsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(bars.ToAsyncEnumerable());
        return source;
    }

    private static IBarStrategy CreateStrategy(params StrategyAction?[] actions)
    {
        var strategy = Substitute.For<IBarStrategy>();
        var callIndex = 0;
        strategy.OnBar(Arg.Any<StrategyContext>())
            .Returns(_ => callIndex < actions.Length ? actions[callIndex++] : null);
        return strategy;
    }

    [Fact]
    public async Task RunAsync_EmptyBars_ReturnsEmptyResult()
    {
        var source = CreateBarSource();
        var strategy = CreateStrategy();

        var result = await _engine.RunAsync(source, strategy, CreateOptions());

        Assert.Empty(result.Fills);
        Assert.Empty(result.Bars);
        Assert.Equal(100_000m, result.FinalPortfolio.Cash);
    }

    [Fact]
    public async Task RunAsync_StrategyReturnsNull_NoOrders()
    {
        var bars = TestBars.CreateSequence(3);
        var source = CreateBarSource(bars);
        var strategy = CreateStrategy(null, null, null);

        var result = await _engine.RunAsync(source, strategy, CreateOptions());

        Assert.Empty(result.Fills);
        Assert.Equal(3, result.Bars.Count);
        _barMatcher.DidNotReceive().TryFill(Arg.Any<Order>(), Arg.Any<OhlcvBar>(), Arg.Any<BacktestOptions>());
    }

    [Fact]
    public async Task RunAsync_OrderOnBarN_FillsOnBarNPlus1()
    {
        var bars = TestBars.CreateSequence(3);
        var source = CreateBarSource(bars);
        var action = StrategyAction.MarketBuy(TestAssets.Aapl, 100m);
        var strategy = CreateStrategy(action, null, null);
        var fill = TestFills.BuyAapl(101m, 100m);
        _barMatcher.TryFill(Arg.Any<Order>(), Arg.Any<OhlcvBar>(), Arg.Any<BacktestOptions>())
            .Returns(fill);

        var result = await _engine.RunAsync(source, strategy, CreateOptions());

        Assert.Single(result.Fills);
        _barMatcher.Received(1).TryFill(
            Arg.Is<Order>(o => o.Side == OrderSide.Buy && o.Quantity == 100m),
            Arg.Is<OhlcvBar>(b => b == bars[1]),
            Arg.Any<BacktestOptions>());
    }

    [Fact]
    public async Task RunAsync_FillReceived_UpdatesPortfolio()
    {
        var bars = TestBars.CreateSequence(2);
        var source = CreateBarSource(bars);
        var action = StrategyAction.MarketBuy(TestAssets.Aapl, 100m);
        var strategy = CreateStrategy(action, null);
        var fill = new Fill(1, TestAssets.Aapl, bars[1].Timestamp, 150m, 100m, OrderSide.Buy, 5m);
        _barMatcher.TryFill(Arg.Any<Order>(), Arg.Any<OhlcvBar>(), Arg.Any<BacktestOptions>())
            .Returns(fill);

        var result = await _engine.RunAsync(source, strategy, CreateOptions());

        // Cash: 100000 - 150*100*1 - 5 = 84995
        Assert.Equal(84_995m, result.FinalPortfolio.Cash);
        var position = result.FinalPortfolio.GetPosition("AAPL");
        Assert.NotNull(position);
        Assert.Equal(100m, position.Quantity);
    }

    [Fact]
    public async Task RunAsync_OrderRejected_PortfolioUnchanged()
    {
        var bars = TestBars.CreateSequence(2);
        var source = CreateBarSource(bars);
        var action = StrategyAction.LimitBuy(TestAssets.Aapl, 100m, 50m);
        var strategy = CreateStrategy(action, null);
        _barMatcher.TryFill(Arg.Any<Order>(), Arg.Any<OhlcvBar>(), Arg.Any<BacktestOptions>())
            .Returns((Fill?)null);

        var result = await _engine.RunAsync(source, strategy, CreateOptions());

        Assert.Empty(result.Fills);
        Assert.Equal(100_000m, result.FinalPortfolio.Cash);
        Assert.Null(result.FinalPortfolio.GetPosition("AAPL"));
    }

    [Fact]
    public async Task RunAsync_BuyThenSell_RoundTrip()
    {
        var bars = TestBars.CreateSequence(4);
        var source = CreateBarSource(bars);
        var buyAction = StrategyAction.MarketBuy(TestAssets.Aapl, 100m);
        var sellAction = StrategyAction.MarketSell(TestAssets.Aapl, 100m);
        var strategy = CreateStrategy(buyAction, null, sellAction, null);

        var buyFill = new Fill(1, TestAssets.Aapl, bars[1].Timestamp, 100m, 100m, OrderSide.Buy, 0m);
        var sellFill = new Fill(2, TestAssets.Aapl, bars[3].Timestamp, 110m, 100m, OrderSide.Sell, 0m);

        _barMatcher.TryFill(Arg.Any<Order>(), Arg.Any<OhlcvBar>(), Arg.Any<BacktestOptions>())
            .Returns(
                buyFill,
                sellFill);

        var result = await _engine.RunAsync(source, strategy, CreateOptions());

        Assert.Equal(2, result.Fills.Count);
        // Cash: 100000 - 10000 + 11000 = 101000
        Assert.Equal(101_000m, result.FinalPortfolio.Cash);
        Assert.Equal(0m, result.FinalPortfolio.GetPosition("AAPL")!.Quantity);
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var bars = TestBars.CreateSequence(100);
        var source = CreateBarSource(bars);
        var strategy = CreateStrategy();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _engine.RunAsync(source, strategy, CreateOptions(), cts.Token));
    }

    [Fact]
    public async Task RunAsync_MultipleOrders_ProcessesInSequence()
    {
        var bars = TestBars.CreateSequence(5);
        var source = CreateBarSource(bars);
        var buy1 = StrategyAction.MarketBuy(TestAssets.Aapl, 50m);
        var buy2 = StrategyAction.MarketBuy(TestAssets.Aapl, 50m);
        var strategy = CreateStrategy(buy1, null, buy2, null, null);

        var fill1 = new Fill(1, TestAssets.Aapl, bars[1].Timestamp, 100m, 50m, OrderSide.Buy, 0m);
        var fill2 = new Fill(2, TestAssets.Aapl, bars[3].Timestamp, 102m, 50m, OrderSide.Buy, 0m);

        _barMatcher.TryFill(Arg.Any<Order>(), Arg.Any<OhlcvBar>(), Arg.Any<BacktestOptions>())
            .Returns(fill1, fill2);

        var result = await _engine.RunAsync(source, strategy, CreateOptions());

        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(100m, result.FinalPortfolio.GetPosition("AAPL")!.Quantity);
    }

    [Fact]
    public async Task RunAsync_ReturnsBacktestResult_WithAllData()
    {
        var bars = TestBars.CreateSequence(2);
        var source = CreateBarSource(bars);
        var strategy = CreateStrategy(null, null);

        var result = await _engine.RunAsync(source, strategy, CreateOptions());

        Assert.NotNull(result);
        Assert.NotNull(result.FinalPortfolio);
        Assert.NotNull(result.Fills);
        Assert.NotNull(result.Bars);
        Assert.NotNull(result.Metrics);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task RunAsync_CallsMetricsCalculator_WithCorrectArgs()
    {
        var bars = TestBars.CreateSequence(3);
        var source = CreateBarSource(bars);
        var strategy = CreateStrategy(null, null, null);

        await _engine.RunAsync(source, strategy, CreateOptions());

        _metricsCalculator.Received(1).Calculate(
            Arg.Any<IReadOnlyList<Fill>>(),
            Arg.Is<IReadOnlyList<OhlcvBar>>(b => b.Count == 3),
            Arg.Any<Portfolio>(),
            Arg.Is<decimal>(p => p == bars[2].Close),
            Arg.Is<Asset>(a => a == TestAssets.Aapl));
    }

    [Fact]
    public async Task RunAsync_OrderGetsCorrectId()
    {
        var bars = TestBars.CreateSequence(4);
        var source = CreateBarSource(bars);
        var buy1 = StrategyAction.MarketBuy(TestAssets.Aapl, 50m);
        var buy2 = StrategyAction.MarketBuy(TestAssets.Aapl, 50m);
        var strategy = CreateStrategy(buy1, null, buy2, null);

        long capturedId1 = 0, capturedId2 = 0;
        var callCount = 0;
        _barMatcher.TryFill(Arg.Any<Order>(), Arg.Any<OhlcvBar>(), Arg.Any<BacktestOptions>())
            .Returns(ci =>
            {
                var order = ci.ArgAt<Order>(0);
                if (callCount++ == 0) capturedId1 = order.Id;
                else capturedId2 = order.Id;
                return new Fill(order.Id, TestAssets.Aapl, DateTimeOffset.Now, 100m, 50m, OrderSide.Buy, 0m);
            });

        await _engine.RunAsync(source, strategy, CreateOptions());

        Assert.Equal(1, capturedId1);
        Assert.Equal(2, capturedId2);
    }
}
