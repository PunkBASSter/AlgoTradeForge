using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Application.Tests.TestUtilities;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Fitness;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Tests.Optimization;

public sealed class OptimizationTaskExecutorTests
{
    private readonly IOptimizationStrategyFactory _strategyFactory = Substitute.For<IOptimizationStrategyFactory>();
    private readonly IAssetRepository _assetRepository = Substitute.For<IAssetRepository>();
    private readonly IHistoryRepository _historyRepository = Substitute.For<IHistoryRepository>();
    private readonly IMetricsCalculator _metricsCalculator = Substitute.For<IMetricsCalculator>();
    private readonly IOptimizationSpaceProvider _spaceProvider = Substitute.For<IOptimizationSpaceProvider>();
    private readonly IRunRepository _runRepository = Substitute.For<IRunRepository>();
    private readonly ICartesianProductGenerator _cartesianGenerator = Substitute.For<ICartesianProductGenerator>();
    private readonly RunProgressCache _progressCache;

    public OptimizationTaskExecutorTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    private OptimizationTaskExecutor CreateExecutor()
    {
        var engine = new BacktestEngine(
            Substitute.For<IBarMatcher>(), new OrderValidator());

        var helper = new OptimizationSetupHelper(
            engine, _assetRepository, _historyRepository,
            _metricsCalculator, _spaceProvider, _runRepository,
            NullLogger<OptimizationSetupHelper>.Instance);

        var timeoutOptions = Options.Create(new RunTimeoutOptions());

        return new OptimizationTaskExecutor(
            _strategyFactory, helper, _cartesianGenerator,
            _progressCache, timeoutOptions,
            NullLogger<OptimizationTaskExecutor>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_respects_cancellation_token()
    {
        // Arrange: set up minimal mocks
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));
        _historyRepository.Load(Arg.Any<DataFeedSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestBars.CreateSeries(10));
        _historyRepository.Load(Arg.Any<Asset>(), Arg.Any<DataFeedSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestBars.CreateSeries(10));

        // Generator returns many combinations to ensure we can cancel mid-stream
        var combos = Enumerable.Range(0, 1000)
            .Select(i => new ParameterCombination(new Dictionary<string, object> { ["Period"] = i }))
            .ToList();
        _cartesianGenerator.Enumerate(Arg.Any<IReadOnlyList<ResolvedAxis>>())
            .Returns(combos);

        var executor = CreateExecutor();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Immediately cancelled

        var ctx = MakeContext(estimatedCount: 1000);

        // Act + Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(ctx, Guid.NewGuid(), 0, cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_records_failed_trial_when_strategy_throws()
    {
        // Arrange: strategy factory throws for every creation
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));
        _historyRepository.Load(Arg.Any<DataFeedSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestBars.CreateSeries(10));
        _historyRepository.Load(Arg.Any<Asset>(), Arg.Any<DataFeedSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestBars.CreateSeries(10));

        _strategyFactory.Create(Arg.Any<string>(), Arg.Any<ParameterCombination>())
            .Returns(_ => throw new InvalidOperationException("Strategy setup failed"));

        var combos = new[]
        {
            new ParameterCombination(new Dictionary<string, object> { ["Period"] = 10 }),
            new ParameterCombination(new Dictionary<string, object> { ["Period"] = 20 }),
        };
        _cartesianGenerator.Enumerate(Arg.Any<IReadOnlyList<ResolvedAxis>>()).Returns(combos);

        var executor = CreateExecutor();
        var ctx = MakeContext(estimatedCount: 2);

        // Act
        var result = await executor.ExecuteAsync(ctx, Guid.NewGuid(), 0, CancellationToken.None);

        // Assert: both combos should be recorded as failed
        Assert.Equal(2, result.FailedTrials);
        Assert.Equal(2, result.ProcessedCount);
        Assert.Empty(result.Trials);
    }

    [Fact]
    public async Task ExecuteAsync_returns_structured_result_for_completed_trial()
    {
        // Arrange: strategy mock does nothing (no orders) — backtest completes with empty metrics
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));
        _historyRepository.Load(Arg.Any<DataFeedSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestBars.CreateSeries(10));
        _historyRepository.Load(Arg.Any<Asset>(), Arg.Any<DataFeedSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestBars.CreateSeries(10));

        var strategy = Substitute.For<IInt64BarStrategy>();
        // Use a real backing list so .Clear()/.Add() actually mutates — the substitute's
        // auto-stub does nothing, which would mismatch BacktestEngine's series.Length assertion.
        strategy.DataSubscriptions.Returns(new List<DataFeedSubscription>());
        _strategyFactory.Create(Arg.Any<string>(), Arg.Any<ParameterCombination>())
            .Returns(strategy);
        _metricsCalculator.Calculate(
                Arg.Any<IReadOnlyList<Fill>>(),
                Arg.Any<IReadOnlyList<long>>(),
                Arg.Any<long>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>())
            .Returns((DefaultMetrics(), Array.Empty<ClosedTrade>()));

        var combos = new[]
        {
            new ParameterCombination(new Dictionary<string, object> { ["Period"] = 10 }),
        };
        _cartesianGenerator.Enumerate(Arg.Any<IReadOnlyList<ResolvedAxis>>()).Returns(combos);

        var executor = CreateExecutor();
        var ctx = MakeContext(estimatedCount: 1);

        // Act
        var result = await executor.ExecuteAsync(ctx, Guid.NewGuid(), 0, CancellationToken.None);

        // Assert: result has correct structure
        Assert.Equal(1, result.ProcessedCount);
        Assert.Equal(0, result.FailedTrials);
        Assert.True(result.DurationMs >= 0);
        Assert.NotNull(result.Trials);
    }

    [Fact]
    public async Task ExecuteAsync_flushes_progress_after_completion()
    {
        // Arrange
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));
        _historyRepository.Load(Arg.Any<DataFeedSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestBars.CreateSeries(10));
        _historyRepository.Load(Arg.Any<Asset>(), Arg.Any<DataFeedSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(TestBars.CreateSeries(10));
        var strategy = Substitute.For<IInt64BarStrategy>();
        // Use a real backing list so .Clear()/.Add() actually mutates — the substitute's
        // auto-stub does nothing, which would mismatch BacktestEngine's series.Length assertion.
        strategy.DataSubscriptions.Returns(new List<DataFeedSubscription>());
        _strategyFactory.Create(Arg.Any<string>(), Arg.Any<ParameterCombination>())
            .Returns(strategy);
        _metricsCalculator.Calculate(
                Arg.Any<IReadOnlyList<Fill>>(),
                Arg.Any<IReadOnlyList<long>>(),
                Arg.Any<long>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>())
            .Returns((DefaultMetrics(), Array.Empty<ClosedTrade>()));

        var combos = Enumerable.Range(0, 5)
            .Select(i => new ParameterCombination(new Dictionary<string, object> { ["Period"] = i }))
            .ToList();
        _cartesianGenerator.Enumerate(Arg.Any<IReadOnlyList<ResolvedAxis>>()).Returns(combos);

        var executor = CreateExecutor();
        var runId = Guid.NewGuid();
        var ctx = MakeContext(estimatedCount: 5);

        // Act
        await executor.ExecuteAsync(ctx, runId, 0, CancellationToken.None);

        // Assert: final progress flush should set processed = total
        var progress = await _progressCache.GetProgressAsync(runId, CancellationToken.None);
        Assert.NotNull(progress);
        Assert.Equal(5, progress.Value.Processed);
        Assert.Equal(5, progress.Value.Total);
    }

    private static OptimizationExecutionContext MakeContext(long estimatedCount = 1000) => new()
    {
        StrategyName = "TestStrategy",
        OptimizationMethod = "BruteForce",
        BacktestSettings = new BacktestSettingsDto
        {
            InitialCash = 10_000m,
            StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        },
        Subscriptions = [new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
        ActiveAxes = [new ResolvedNumericAxis("Period", [10, 20])],
        EstimatedCount = estimatedCount,
        MaxParallelism = 1,
        MaxTrialsToKeep = 100,
        FilterOptions = new NoFilter(),
        FitnessConfig = new FitnessConfig(),
        Normalizer = null,
        GroupId = Guid.NewGuid(),
        GroupRunKey = "test-key",
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static PerformanceMetrics DefaultMetrics() => new()
    {
        TotalTrades = 0, WinningTrades = 0, LosingTrades = 0,
        NetProfit = 0m, GrossProfit = 0m, GrossLoss = 0m,
        TotalCommissions = 0m, TotalReturnPct = 0, AnnualizedReturnPct = 0,
        SharpeRatio = 0, SortinoRatio = 0, MaxDrawdownPct = 0,
        WinRatePct = 0, ProfitFactor = 0, AverageWin = 0, AverageLoss = 0,
        InitialCapital = 10_000m, FinalEquity = 10_000m, TradingDays = 0,
    };

    private sealed class NoFilter : ITrialFilterOptions
    {
        public double? MinProfitFactor => null;
        public double? MaxDrawdownPct => null;
        public double? MinSharpeRatio => null;
        public double? MinSortinoRatio => null;
        public double? MinAnnualizedReturnPct => null;
        public int? MinTradeCount => null;
        public decimal? MinNetProfit => null;
    }
}
