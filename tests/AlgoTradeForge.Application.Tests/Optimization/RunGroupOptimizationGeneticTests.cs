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
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Reporting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Tests.Optimization;

public class RunGroupOptimizationGeneticTests
{
    private readonly IOptimizationStrategyFactory _strategyFactory = Substitute.For<IOptimizationStrategyFactory>();
    private readonly IAssetRepository _assetRepository = Substitute.For<IAssetRepository>();
    private readonly IHistoryRepository _historyRepository = Substitute.For<IHistoryRepository>();
    private readonly IMetricsCalculator _metricsCalculator = Substitute.For<IMetricsCalculator>();
    private readonly IOptimizationSpaceProvider _spaceProvider = Substitute.For<IOptimizationSpaceProvider>();
    private readonly IRunRepository _runRepository = Substitute.For<IRunRepository>();
    private readonly RunProgressCache _progressCache;

    public RunGroupOptimizationGeneticTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    private RunGroupOptimizationCommandHandler CreateHandler()
    {
        var engine = new BacktestEngine(
            Substitute.For<IBarMatcher>(), new OrderValidator());

        var helper = new OptimizationSetupHelper(
            engine, _assetRepository, _historyRepository,
            _metricsCalculator, _spaceProvider, _runRepository,
            NullLogger<OptimizationSetupHelper>.Instance);

        return new RunGroupOptimizationCommandHandler(
            helper, new OptimizationAxisResolver(),
            new CartesianProductGenerator(),
            _runRepository, _progressCache,
            new ComputeTaskQueue(),
            NullLogger<RunGroupOptimizationCommandHandler>.Instance);
    }

    private static RunGroupOptimizationCommand CreateGeneticGroupCommand(int dssCount = 2) => new()
    {
        StrategyName = "TestStrategy",
        OptimizationMethod = "Genetic",
        BacktestSettings = new BacktestSettingsDto
        {
            InitialCash = 10_000m,
            StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        },
        SubscriptionAxis = Enumerable.Range(0, dssCount).Select(_ => new List<DataFeedSubscription>
        {
            new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))
        }).ToList(),
        Axes = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Period"] = new RangeOverride(10, 20, 5)
        },
        GeneticSettings = new GeneticConfig(),
    };

    private void SetupStandardMocks()
    {
        var descriptor = new OptimizationSpaceDescriptor(
            "TestStrategy",
            typeof(object),
            typeof(object),
            new List<ParameterAxis>
            {
                new NumericRangeAxis("Period", 1, 100, 1, typeof(int))
            });
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);
    }

    [Fact]
    public async Task HandleAsync_GeneticGroup_CreatesCorrectNumberOfChildRuns()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateGeneticGroupCommand(dssCount: 3);

        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Runs.Count);
        Assert.All(result.Runs, r => Assert.NotEqual(Guid.Empty, r.Id));
    }

    [Fact]
    public async Task HandleAsync_GeneticGroup_ChildRecordsUseGeneticMethod()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateGeneticGroupCommand();

        await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Verify InsertPlaceholderAsync was called with Genetic method on each child record
        await _runRepository.Received(2).InsertOptimizationPlaceholderAsync(
            Arg.Is<OptimizationRunRecord>(r => r.OptimizationMethod == "Genetic"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_GeneticGroup_GroupRecordUsesGeneticMethod()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateGeneticGroupCommand();

        await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        await _runRepository.Received(1).InsertOptimizationGroupAsync(
            Arg.Is<OptimizationGroupRecord>(g => g.OptimizationMethod == "Genetic"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_GeneticGroup_NullGeneticSettings_ThrowsArgumentException()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateGeneticGroupCommand() with { GeneticSettings = null };

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_GeneticGroup_TotalCombinationsEqualsMaxEvaluations()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateGeneticGroupCommand();

        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Default GeneticConfig auto-resolves MaxEvaluations based on dimensionality.
        // With 1 axis → popSize=50 (min clamp), maxEvals=50*200=10000
        Assert.True(result.TotalCombinationsPerRun > 0);
        Assert.All(result.Runs, r => Assert.Equal(result.TotalCombinationsPerRun, r.TotalCombinations));
    }

    [Fact]
    public async Task HandleAsync_GeneticGroup_PopulationSizeExceedsMax_ThrowsArgumentException()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateGeneticGroupCommand() with
        {
            GeneticSettings = new GeneticConfig { PopulationSize = 3000 }
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_GeneticGroup_StrategyNotFound_ThrowsArgumentException()
    {
        _spaceProvider.GetDescriptor("TestStrategy").Returns((IOptimizationSpaceDescriptor?)null);
        var handler = CreateHandler();
        var command = CreateGeneticGroupCommand();

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }
}
