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

public class RunOptimizationCommandHandlerTests
{
    private readonly IOptimizationStrategyFactory _strategyFactory = Substitute.For<IOptimizationStrategyFactory>();
    private readonly IAssetRepository _assetRepository = Substitute.For<IAssetRepository>();
    private readonly IHistoryRepository _historyRepository = Substitute.For<IHistoryRepository>();
    private readonly IMetricsCalculator _metricsCalculator = Substitute.For<IMetricsCalculator>();
    private readonly IOptimizationSpaceProvider _spaceProvider = Substitute.For<IOptimizationSpaceProvider>();
    private readonly ICartesianProductGenerator _cartesianGenerator = Substitute.For<ICartesianProductGenerator>();
    private readonly IRunRepository _runRepository = Substitute.For<IRunRepository>();
    private readonly IRunCancellationRegistry _cancellationRegistry = new InMemoryRunCancellationRegistry();
    private readonly RunProgressCache _progressCache;

    public RunOptimizationCommandHandlerTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    private RunOptimizationCommandHandler CreateHandler()
    {
        var engine = new BacktestEngine(
            Substitute.For<IBarMatcher>(), new OrderValidator());

        return new RunOptimizationCommandHandler(
            engine, _strategyFactory, _assetRepository, _historyRepository,
            _metricsCalculator, _spaceProvider, new OptimizationAxisResolver(),
            _cartesianGenerator, _runRepository, _progressCache,
            _cancellationRegistry,
            Options.Create(new RunTimeoutOptions()),
            NullLogger<RunOptimizationCommandHandler>.Instance);
    }

    private static RunOptimizationCommand CreateCommand() => new()
    {
        StrategyName = "TestStrategy",
        InitialCash = 10_000m,
        StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
        DataSubscriptions =
        [
            new DataSubscriptionDto { Asset = "BTCUSDT", Exchange = "Binance", TimeFrame = "01:00:00" }
        ],
        SubscriptionAxis = null,
        Axes = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Period"] = new RangeOverride(10, 20, 5)
        }
    };

    private void SetupStandardMocks(long estimatedCount = 3)
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

        _cartesianGenerator.EstimateCount(Arg.Any<IReadOnlyList<ResolvedAxis>>())
            .Returns(estimatedCount);
        _cartesianGenerator.Enumerate(Arg.Any<IReadOnlyList<ResolvedAxis>>())
            .Returns(new List<ParameterCombination>
            {
                new(new Dictionary<string, object> { ["Period"] = 10 }),
                new(new Dictionary<string, object> { ["Period"] = 15 }),
                new(new Dictionary<string, object> { ["Period"] = 20 }),
            });
    }

    [Fact]
    public async Task HandleAsync_DedupHit_ReturnsExistingId()
    {
        // Arrange
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        var existingId = Guid.NewGuid();
        var runKey = RunKeyBuilder.Build(command);
        await _progressCache.SetRunKeyAsync(runKey, existingId, TestContext.Current.CancellationToken);
        await _progressCache.SetProgressAsync(existingId, 1, 3, TestContext.Current.CancellationToken);

        // Act
        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(existingId, result.Id);
    }

    [Fact]
    public async Task HandleAsync_NewRun_ReturnsSubmissionWithCorrectTotalCombinations()
    {
        // Arrange
        SetupStandardMocks(estimatedCount: 3);
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act
        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, result.TotalCombinations);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task HandleAsync_NewRun_CreatesProgressEntryAndSetsRunKey()
    {
        // Arrange
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act
        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Assert
        var progress = await _progressCache.GetProgressAsync(result.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(progress);
        Assert.Equal(3, progress.Value.Total);

        var runKey = RunKeyBuilder.Build(command);
        var mappedId = await _progressCache.TryGetRunIdByKeyAsync(runKey, TestContext.Current.CancellationToken);
        Assert.Equal(result.Id, mappedId);
    }

    [Fact]
    public async Task HandleAsync_StrategyNotFound_ThrowsArgumentException()
    {
        // Arrange — spaceProvider returns null for unknown strategy
        _spaceProvider.GetDescriptor("TestStrategy").Returns((IOptimizationSpaceDescriptor?)null);
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_NoDataSubscriptions_ThrowsArgumentException()
    {
        // Arrange
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand() with { DataSubscriptions = [], SubscriptionAxis = [] };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_MaxCombinationsExceeded_ThrowsArgumentException()
    {
        // Arrange
        SetupStandardMocks(estimatedCount: 200_000);
        var handler = CreateHandler();
        var command = CreateCommand() with { MaxCombinations = 100_000 };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleAsync_StaleDedup_CleansUpAndCreatesNewRun()
    {
        // Arrange
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        var staleId = Guid.NewGuid();
        var runKey = RunKeyBuilder.Build(command);
        await _progressCache.SetRunKeyAsync(runKey, staleId, TestContext.Current.CancellationToken);
        // No progress entry for staleId — stale mapping

        // Act
        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEqual(staleId, result.Id);
    }

    // ── Subscription resolution path tests ──────────────────────────

    private void SetupAssetMock(string name, string exchange, Asset asset)
    {
        _assetRepository.GetByNameAsync(name, exchange, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Asset?>(asset));
    }

    private void SetupStandardMocksWithEth(long estimatedCount = 3)
    {
        SetupStandardMocks(estimatedCount);
        SetupAssetMock("ETHUSDT", "Binance", TestAssets.EthUsdt);
    }

    private static DataSubscriptionDto BtcSub => new() { Asset = "BTCUSDT", Exchange = "Binance", TimeFrame = "01:00:00" };
    private static DataSubscriptionDto EthSub => new() { Asset = "ETHUSDT", Exchange = "Binance", TimeFrame = "01:00:00" };

    [Fact]
    public async Task HandleAsync_SingleDataSubscription_NoAxis_FixedSingleFeed()
    {
        // Arrange — single fixed sub, no axis → no discrete axis injected
        SetupStandardMocks(estimatedCount: 3);
        var handler = CreateHandler();
        var command = CreateCommand() with
        {
            DataSubscriptions = [BtcSub],
            SubscriptionAxis = null,
        };

        // Act
        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Assert — EstimateCount called with parameter axes only (no DataSubscriptions axis)
        Assert.Equal(3, result.TotalCombinations);
        _cartesianGenerator.Received().EstimateCount(
            Arg.Is<IReadOnlyList<ResolvedAxis>>(axes =>
                !axes.OfType<ResolvedDiscreteAxis>().Any(d => d.Name == "DataSubscriptions")));
    }

    [Fact]
    public async Task HandleAsync_MultipleDataSubscriptions_NoAxis_BackwardCompatDiscreteAxis()
    {
        // Arrange — two subs, no axis → backward-compat discrete axis with 2 values
        SetupStandardMocksWithEth(estimatedCount: 6);
        var handler = CreateHandler();
        var command = CreateCommand() with
        {
            DataSubscriptions = [BtcSub, EthSub],
            SubscriptionAxis = null,
        };

        // Act
        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Assert — axes list includes a ResolvedDiscreteAxis("DataSubscriptions") with 2 values
        Assert.Equal(6, result.TotalCombinations);
        _cartesianGenerator.Received().EstimateCount(
            Arg.Is<IReadOnlyList<ResolvedAxis>>(axes =>
                axes.OfType<ResolvedDiscreteAxis>()
                    .Any(d => d.Name == "DataSubscriptions" && d.Values.Count == 2)));
    }

    [Fact]
    public async Task HandleAsync_SubscriptionAxisOnly_DiscreteAxisOverSubscriptions()
    {
        // Arrange — no fixed subs, axis only → discrete axis with 2 values
        SetupStandardMocksWithEth(estimatedCount: 6);
        var handler = CreateHandler();
        var command = CreateCommand() with
        {
            DataSubscriptions = null,
            SubscriptionAxis = [BtcSub, EthSub],
        };

        // Act
        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(6, result.TotalCombinations);
        _cartesianGenerator.Received().EstimateCount(
            Arg.Is<IReadOnlyList<ResolvedAxis>>(axes =>
                axes.OfType<ResolvedDiscreteAxis>()
                    .Any(d => d.Name == "DataSubscriptions" && d.Values.Count == 2)));
    }

    [Fact]
    public async Task HandleAsync_FixedPlusAxis_BothPresent()
    {
        // Arrange — BTC fixed + ETH as axis → discrete axis with 1 value (ETH only)
        SetupStandardMocksWithEth(estimatedCount: 3);
        var handler = CreateHandler();
        var command = CreateCommand() with
        {
            DataSubscriptions = [BtcSub],
            SubscriptionAxis = [EthSub],
        };

        // Act
        var result = await handler.HandleAsync(command, TestContext.Current.CancellationToken);

        // Assert — axis has 1 value (only the axis entry, not the fixed one)
        Assert.Equal(3, result.TotalCombinations);
        _cartesianGenerator.Received().EstimateCount(
            Arg.Is<IReadOnlyList<ResolvedAxis>>(axes =>
                axes.OfType<ResolvedDiscreteAxis>()
                    .Any(d => d.Name == "DataSubscriptions" && d.Values.Count == 1)));
    }

    [Fact]
    public async Task HandleAsync_BothEmpty_ThrowsArgumentException()
    {
        // Arrange — both explicitly empty → should throw
        SetupStandardMocks();
        var handler = CreateHandler();
        var command = CreateCommand() with
        {
            DataSubscriptions = [],
            SubscriptionAxis = [],
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(command, TestContext.Current.CancellationToken));
    }
}
