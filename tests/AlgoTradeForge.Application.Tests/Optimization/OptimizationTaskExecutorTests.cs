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
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

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
        _historyRepository.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
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

        var ctx = new OptimizationExecutionContext
        {
            StrategyName = "TestStrategy",
            OptimizationMethod = "BruteForce",
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = 10_000m,
                StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                EndTime = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
            },
            SubscriptionDtos = [new DataSubscriptionDto { AssetName = "BTCUSDT", Exchange = "Binance", TimeFrame = "01:00:00" }],
            ActiveAxes = [new ResolvedNumericAxis("Period", [10, 20])],
            EstimatedCount = 1000,
            MaxParallelism = 1,
            MaxTrialsToKeep = 100,
            FilterOptions = new NoFilter(),
            FitnessConfig = new FitnessConfig(),
            Normalizer = null,
            GroupId = Guid.NewGuid(),
            GroupRunKey = "test-key",
            StartedAt = DateTimeOffset.UtcNow,
        };

        // Act + Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(ctx, Guid.NewGuid(), 0, cts.Token));
    }

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
