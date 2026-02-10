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
            Arg.Any<IReadOnlyList<IntBar>>(),
            Arg.Any<Portfolio>(),
            Arg.Any<long>(),
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

    private static BacktestOptions CreateOptions() =>
        new()
        {
            InitialCash = 100_000m,
            Asset = TestAssets.Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
            CommissionPerTrade = 0m
        };

    [Fact]
    public async Task RunAsync_EmptyBars_ReturnsEmptyResult()
    {
        var bars = TestBars.CreateSeries(0);
        var strategy = Substitute.For<IIntBarStrategy>();

        var result = await _engine.RunAsync(bars, strategy, CreateOptions());

        Assert.Empty(result.Fills);
        Assert.Empty(result.Bars);
        Assert.Equal(100_000m, result.FinalPortfolio.Cash);
    }

    [Fact]
    public async Task RunAsync_IteratesBars_CallsStrategy()
    {
        var bars = TestBars.CreateSeries(3);
        var strategy = Substitute.For<IIntBarStrategy>();

        var result = await _engine.RunAsync(bars, strategy, CreateOptions());

        Assert.Equal(3, result.Bars.Count);
        strategy.Received(3).OnBarComplete(Arg.Any<TimeSeries<IntBar>>());
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var bars = TestBars.CreateSeries(100);
        var strategy = Substitute.For<IIntBarStrategy>();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _engine.RunAsync(bars, strategy, CreateOptions(), cts.Token));
    }

    [Fact]
    public async Task RunAsync_ReturnsMetrics()
    {
        var bars = TestBars.CreateSeries(3);
        var strategy = Substitute.For<IIntBarStrategy>();

        var result = await _engine.RunAsync(bars, strategy, CreateOptions());

        Assert.NotNull(result.Metrics);
        _metricsCalculator.Received(1).Calculate(
            Arg.Any<IReadOnlyList<Fill>>(),
            Arg.Is<IReadOnlyList<IntBar>>(b => b.Count == 3),
            Arg.Any<Portfolio>(),
            Arg.Any<long>(),
            Arg.Is<Asset>(a => a == TestAssets.Aapl));
    }

    [Fact]
    public async Task RunAsync_ReturnsDuration()
    {
        var bars = TestBars.CreateSeries(2);
        var strategy = Substitute.For<IIntBarStrategy>();

        var result = await _engine.RunAsync(bars, strategy, CreateOptions());

        Assert.True(result.Duration >= TimeSpan.Zero);
    }
}
