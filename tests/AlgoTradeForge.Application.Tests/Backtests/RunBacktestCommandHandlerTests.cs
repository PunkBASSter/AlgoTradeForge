using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Application.Tests.TestUtilities;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Backtests;

public class RunBacktestCommandHandlerTests
{
    private readonly IAssetRepository _assetRepository = Substitute.For<IAssetRepository>();
    private readonly IStrategyFactory _strategyFactory = Substitute.For<IStrategyFactory>();
    private readonly IHistoryRepository _historyRepository = Substitute.For<IHistoryRepository>();
    private readonly IMetricsCalculator _metricsCalculator = Substitute.For<IMetricsCalculator>();
    private readonly IRunSinkFactory _runSinkFactory = Substitute.For<IRunSinkFactory>();
    private readonly IPostRunPipeline _postRunPipeline = Substitute.For<IPostRunPipeline>();
    private readonly IRunRepository _runRepository = Substitute.For<IRunRepository>();
    private readonly IRunCancellationRegistry _cancellationRegistry = new InMemoryRunCancellationRegistry();
    private readonly RunProgressCache _progressCache;

    public RunBacktestCommandHandlerTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    private RunBacktestCommandHandler CreateHandler()
    {
        var engine = new BacktestEngine(
            Substitute.For<IBarMatcher>(),
            Substitute.For<IRiskEvaluator>());

        var preparer = new BacktestPreparer(
            _assetRepository, _strategyFactory, _historyRepository);

        return new RunBacktestCommandHandler(
            engine, preparer, _metricsCalculator,
            _runSinkFactory, _postRunPipeline, _runRepository,
            _progressCache, _cancellationRegistry,
            Options.Create(new RunTimeoutOptions()),
            NullLogger<RunBacktestCommandHandler>.Instance);
    }

    private static RunBacktestCommand CreateCommand() => new()
    {
        AssetName = "BTCUSDT",
        Exchange = "Binance",
        StrategyName = "TestStrategy",
        InitialCash = 10_000m,
        StartTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EndTime = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private void SetupPreparerMocks()
    {
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Domain.Asset?>(asset));

        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.Version.Returns("1.0");
        strategy.DataSubscriptions.Returns(new List<DataSubscription>
        {
            new(asset, TimeSpan.FromMinutes(1))
        });

        _strategyFactory.Create("TestStrategy", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var series = TestBars.CreateSeries(10);
        _historyRepository.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(series);
    }

    [Fact]
    public async Task HandleAsync_DedupHit_ReturnsExistingId()
    {
        // Arrange
        SetupPreparerMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        var existingId = Guid.NewGuid();
        var runKey = RunKeyBuilder.Build(command);
        await _progressCache.SetRunKeyAsync(runKey, existingId);
        await _progressCache.SetProgressAsync(existingId, 5, 10);

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        Assert.Equal(existingId, result.Id);
    }

    [Fact]
    public async Task HandleAsync_NewRun_ReturnsSubmissionWithTotalBars()
    {
        // Arrange
        SetupPreparerMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        Assert.Equal(10, result.TotalBars);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task HandleAsync_NewRun_CreatesProgressEntryInCache()
    {
        // Arrange
        SetupPreparerMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        var progress = await _progressCache.GetProgressAsync(result.Id);
        Assert.NotNull(progress);
        Assert.Equal(10, progress.Value.Total);
    }

    [Fact]
    public async Task HandleAsync_NewRun_SetsRunKeyMapping()
    {
        // Arrange
        SetupPreparerMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        var runKey = RunKeyBuilder.Build(command);
        var mappedId = await _progressCache.TryGetRunIdByKeyAsync(runKey);
        Assert.Equal(result.Id, mappedId);
    }

    [Fact]
    public async Task HandleAsync_StaleDedup_CleansUpAndCreatesNewRun()
    {
        // Arrange
        SetupPreparerMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        // Set up a stale RunKey mapping (no matching progress entry)
        var staleId = Guid.NewGuid();
        var runKey = RunKeyBuilder.Build(command);
        await _progressCache.SetRunKeyAsync(runKey, staleId);

        // Act
        var result = await handler.HandleAsync(command);

        // Assert — new run created, not the stale ID
        Assert.NotEqual(staleId, result.Id);
    }

    [Fact]
    public async Task HandleAsync_AssetNotFound_ThrowsArgumentException()
    {
        // Arrange — don't set up asset repository mock (returns null)
        var handler = CreateHandler();
        var command = CreateCommand();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(command));
    }

    // --- Background execution path tests ---

    private void SetupBackgroundMocks()
    {
        var sink = Substitute.For<IRunSink>();
        sink.RunFolderPath.Returns("/test/run/path");
        _runSinkFactory.Create(Arg.Any<RunIdentity>()).Returns(sink);

        _metricsCalculator.Calculate(
                Arg.Any<IReadOnlyList<Fill>>(), Arg.Any<IReadOnlyList<long>>(),
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns(new PerformanceMetrics
            {
                TotalTrades = 0, WinningTrades = 0, LosingTrades = 0,
                NetProfit = 0, GrossProfit = 0, GrossLoss = 0, TotalCommissions = 0,
                TotalReturnPct = 0, AnnualizedReturnPct = 0,
                SharpeRatio = 0, SortinoRatio = 0, MaxDrawdownPct = 0,
                WinRatePct = 0, ProfitFactor = 0, AverageWin = 0, AverageLoss = 0,
                InitialCapital = 10_000m, FinalEquity = 10_000m, TradingDays = 0,
            });

        _postRunPipeline.Execute(Arg.Any<string>(), Arg.Any<RunIdentity>(), Arg.Any<RunSummary>())
            .Returns(new PostRunResult(true, true, null, null));
    }

    private async Task WaitForBackgroundCompletion(Guid runId, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var progress = await _progressCache.GetProgressAsync(runId);
            if (progress is null) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"Background task for run {runId} did not complete within {timeoutMs}ms.");
    }

    [Fact]
    public async Task BackgroundTask_CompletesSuccessfully_SavesRecordAndCleansUp()
    {
        // Arrange
        SetupPreparerMocks();
        SetupBackgroundMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        BacktestRunRecord? savedRecord = null;
        _runRepository.SaveAsync(Arg.Any<BacktestRunRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => savedRecord = ci.Arg<BacktestRunRecord>());

        // Act
        var submission = await handler.HandleAsync(command);
        await WaitForBackgroundCompletion(submission.Id);

        // Assert — record was saved with correct data
        Assert.NotNull(savedRecord);
        Assert.Equal(submission.Id, savedRecord.Id);
        Assert.Equal(RunModes.Backtest, savedRecord.RunMode);
        Assert.Null(savedRecord.ErrorMessage);
        Assert.Equal(10, savedRecord.TotalBars);

        // Assert — progress and run key were cleaned up
        var progress = await _progressCache.GetProgressAsync(submission.Id);
        Assert.Null(progress);
        var runKey = RunKeyBuilder.Build(command);
        var mappedId = await _progressCache.TryGetRunIdByKeyAsync(runKey);
        Assert.Null(mappedId);
    }

    [Fact]
    public async Task BackgroundTask_EngineThrows_SavesErrorRecord()
    {
        // Arrange — make metricsCalculator throw to simulate a failure in the background task
        SetupPreparerMocks();
        SetupBackgroundMocks();
        _metricsCalculator.Calculate(
                Arg.Any<IReadOnlyList<Fill>>(), Arg.Any<IReadOnlyList<long>>(),
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Throws(new InvalidOperationException("Simulated engine failure"));

        var handler = CreateHandler();
        var command = CreateCommand();

        BacktestRunRecord? savedRecord = null;
        _runRepository.SaveAsync(Arg.Any<BacktestRunRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => savedRecord = ci.Arg<BacktestRunRecord>());

        // Act
        var submission = await handler.HandleAsync(command);
        await WaitForBackgroundCompletion(submission.Id);

        // Assert — error record was saved
        Assert.NotNull(savedRecord);
        Assert.Equal(submission.Id, savedRecord.Id);
        Assert.Equal(RunModes.Failed, savedRecord.RunMode);
        Assert.Equal("Simulated engine failure", savedRecord.ErrorMessage);

        // Assert — cleanup still happened
        var progress = await _progressCache.GetProgressAsync(submission.Id);
        Assert.Null(progress);
    }

    [Fact]
    public async Task BackgroundTask_Cancelled_SavesCancelledRecord()
    {
        // Arrange — strategy blocks on first bar so we can cancel reliably
        var asset = TestAssets.BtcUsdt;
        _assetRepository.GetByNameAsync("BTCUSDT", "Binance", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Domain.Asset?>(asset));

        var enteredBar = new ManualResetEventSlim(false);
        var strategy = Substitute.For<IInt64BarStrategy>();
        strategy.Version.Returns("1.0");
        strategy.DataSubscriptions.Returns(new List<DataSubscription>
        {
            new(asset, TimeSpan.FromMinutes(1))
        });
        // Block engine briefly on OnBarComplete — long enough for the test to cancel,
        // short enough that the engine reaches ct.ThrowIfCancellationRequested() between bars
        strategy.When(s => s.OnBarComplete(
                Arg.Any<Domain.History.Int64Bar>(),
                Arg.Any<DataSubscription>(),
                Arg.Any<Domain.Strategy.IOrderContext>()))
            .Do(_ =>
            {
                enteredBar.Set();
                Thread.Sleep(300);
            });

        _strategyFactory.Create("TestStrategy", Arg.Any<IIndicatorFactory>(), Arg.Any<IDictionary<string, object>?>())
            .Returns(strategy);

        var series = TestBars.CreateSeries(10);
        _historyRepository.Load(Arg.Any<DataSubscription>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(series);

        SetupBackgroundMocks();
        var handler = CreateHandler();
        var command = CreateCommand();

        BacktestRunRecord? savedRecord = null;
        _runRepository.SaveAsync(Arg.Any<BacktestRunRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => savedRecord = ci.Arg<BacktestRunRecord>());

        // Act — submit, wait for engine to start processing, then cancel
        var submission = await handler.HandleAsync(command);
        enteredBar.Wait(TimeSpan.FromSeconds(5));
        _cancellationRegistry.TryCancel(submission.Id);
        await WaitForBackgroundCompletion(submission.Id);

        // Assert — cancelled record was saved
        Assert.NotNull(savedRecord);
        Assert.Equal(submission.Id, savedRecord.Id);
        Assert.Equal(RunModes.Cancelled, savedRecord.RunMode);
        Assert.Contains("cancelled", savedRecord.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
