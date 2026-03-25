using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Application.Tests.TestUtilities;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Genetic;
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

public class RunGeneticOptimizationCommandHandlerTests
{
    private readonly IOptimizationStrategyFactory _strategyFactory = Substitute.For<IOptimizationStrategyFactory>();
    private readonly IAssetRepository _assetRepository = Substitute.For<IAssetRepository>();
    private readonly IHistoryRepository _historyRepository = Substitute.For<IHistoryRepository>();
    private readonly IMetricsCalculator _metricsCalculator = Substitute.For<IMetricsCalculator>();
    private readonly IOptimizationSpaceProvider _spaceProvider = Substitute.For<IOptimizationSpaceProvider>();
    private readonly IRunRepository _runRepository = Substitute.For<IRunRepository>();
    private readonly IRunCancellationRegistry _cancellationRegistry = new InMemoryRunCancellationRegistry();
    private readonly RunProgressCache _progressCache;

    public RunGeneticOptimizationCommandHandlerTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    private RunGeneticOptimizationCommandHandler CreateHandler()
    {
        var engine = new BacktestEngine(
            Substitute.For<IBarMatcher>(), new OrderValidator());

        var helper = new OptimizationSetupHelper(
            engine, _assetRepository, _historyRepository,
            _metricsCalculator, _spaceProvider, _runRepository,
            NullLogger<OptimizationSetupHelper>.Instance);

        return new RunGeneticOptimizationCommandHandler(
            _strategyFactory, helper, new OptimizationAxisResolver(),
            _progressCache,
            _cancellationRegistry,
            Options.Create(new RunTimeoutOptions()),
            NullLogger<RunGeneticOptimizationCommandHandler>.Instance);
    }

    private static RunGeneticOptimizationCommand CreateCommand() => new()
    {
        StrategyName = "TestStrategy",
        BacktestSettings = new BacktestSettingsDto
        {
            InitialCash = 10_000m,
            StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndTime = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        },
        DataSubscriptions =
        [
            new DataSubscriptionDto { AssetName = "BTCUSDT", Exchange = "Binance", TimeFrame = "01:00:00" }
        ],
        SubscriptionAxis = null,
        Axes = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Period"] = new RangeOverride(10, 20, 5)
        },
        GeneticSettings = new GeneticConfig(),
    };

    private void SetupStandardMocks()
    {
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));

        var descriptor = new OptimizationSpaceDescriptor(
            "TestStrategy",
            typeof(object),
            typeof(object),
            new List<ParameterAxis>
            {
                new NumericRangeAxis("Period", 1, 100, 1, typeof(int))
            });
        _spaceProvider.GetDescriptor("TestStrategy").Returns(descriptor);

        var series = TestBars.CreateSeries(10);
        _historyRepository.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(series);
    }

    [Fact]
    public async Task HandleAsync_StrategyNotFound_ThrowsArgumentException()
    {
        _spaceProvider.GetDescriptor("TestStrategy").Returns((IOptimizationSpaceDescriptor?)null);
        var handler = CreateHandler();
        var command = CreateCommand();

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_NoDataSubscriptions_ThrowsArgumentException()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand() with { DataSubscriptions = [], SubscriptionAxis = [] };

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_ValidCommand_ReturnsSubmissionWithPositiveTotalCombinations()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.True(result.TotalCombinations > 0);
    }

    [Fact]
    public async Task HandleAsync_PopulationSizeExceedsMax_ThrowsArgumentException()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand() with
        {
            GeneticSettings = new GeneticConfig { PopulationSize = 3000 }
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_MaxGenerationsExceedsMax_ThrowsArgumentException()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand() with
        {
            GeneticSettings = new GeneticConfig { MaxGenerations = 6000 }
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_MaxEvaluationsExceedsMax_ThrowsArgumentException()
    {
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand() with
        {
            GeneticSettings = new GeneticConfig { MaxEvaluations = 2_000_000 }
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }
}
