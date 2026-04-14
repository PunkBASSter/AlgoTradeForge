using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Validation;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Validation;

public sealed class ValidationTaskExecutorTests
{
    private readonly IRunRepository _runRepository = Substitute.For<IRunRepository>();
    private readonly IValidationRepository _validationRepository = Substitute.For<IValidationRepository>();
    private readonly ISimulationCacheFileStore _cacheFileStore = Substitute.For<ISimulationCacheFileStore>();
    private readonly RunProgressCache _progressCache;

    public ValidationTaskExecutorTests()
    {
        var distributedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        _progressCache = new RunProgressCache(distributedCache);
    }

    private ValidationTaskExecutor CreateExecutor() => new(
        _runRepository,
        _validationRepository,
        _progressCache,
        _cacheFileStore,
        Options.Create(new SimulationCacheOptions { SpilloverThresholdBytes = long.MaxValue }),
        NullLogger<ValidationTaskExecutor>.Instance);

    private static ValidationExecutionContext MakeContext(Guid optRunId) => new()
    {
        OptimizationRunId = optRunId,
        StrategyName = "TestStrategy",
        ThresholdProfileName = "Crypto-Standard",
        ThresholdProfileJson = "{}",
        Profile = new ValidationThresholdProfile { Name = "Crypto-Standard" },
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static OptimizationRunRecord MakeOptimizationRecord(
        Guid id, IReadOnlyList<BacktestRunRecord>? trials = null)
    {
        return new OptimizationRunRecord
        {
            Id = id,
            StrategyName = "TestStrategy",
            StrategyVersion = "1.0",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 1000,
            TotalCombinations = 100,
            SortBy = MetricNames.SharpeRatio,
            DataSubscriptions = [new DataSubscriptionDto { AssetName = "BTCUSDT", Exchange = "Binance", TimeFrame = "1h" }],
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = 10_000m,
                StartTime = DateTimeOffset.UtcNow.AddDays(-30),
                EndTime = DateTimeOffset.UtcNow,
            },
            MaxParallelism = 4,
            Trials = trials ?? [],
        };
    }

    [Fact]
    public async Task ExecuteAsync_uses_cached_trials_when_provided()
    {
        // Arrange
        var optId = Guid.NewGuid();
        var validationId = Guid.NewGuid();

        // Return metadata (no trials)
        _runRepository.GetOptimizationByIdAsync(optId, false, false, Arg.Any<CancellationToken>())
            .Returns(MakeOptimizationRecord(optId));

        // Cached trials with NO trade PnL — should complete with empty result
        var cachedTrials = Array.Empty<BacktestRunRecord>();

        var executor = CreateExecutor();
        var ctx = MakeContext(optId);

        // Act
        await executor.ExecuteAsync(ctx, validationId, cachedTrials, CancellationToken.None);

        // Assert: validation saved with empty result (no candidates)
        await _validationRepository.Received(1).SaveAsync(
            Arg.Is<ValidationRunRecord>(r =>
                r.Id == validationId &&
                r.CandidatesIn == 0 &&
                r.Verdict == "Red"),
            Arg.Any<CancellationToken>());

        // Should NOT have loaded full trials from DB
        await _runRepository.DidNotReceive().GetOptimizationByIdAsync(
            optId, false, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_db_load_when_cache_is_null()
    {
        // Arrange
        var optId = Guid.NewGuid();
        var validationId = Guid.NewGuid();

        // Metadata load (no trials)
        _runRepository.GetOptimizationByIdAsync(optId, false, false, Arg.Any<CancellationToken>())
            .Returns(MakeOptimizationRecord(optId));

        // Full load with trials (standalone validation path)
        _runRepository.GetOptimizationByIdAsync(optId, false, true, Arg.Any<CancellationToken>())
            .Returns(MakeOptimizationRecord(optId, trials: []));

        var executor = CreateExecutor();
        var ctx = MakeContext(optId);

        // Act — pass null cache to simulate standalone validation
        await executor.ExecuteAsync(ctx, validationId, cachedTrials: null, CancellationToken.None);

        // Assert: DB load was called for full trials
        await _runRepository.Received(1).GetOptimizationByIdAsync(
            optId, false, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_handles_zero_trials_gracefully()
    {
        // Arrange
        var optId = Guid.NewGuid();
        var validationId = Guid.NewGuid();

        _runRepository.GetOptimizationByIdAsync(optId, false, false, Arg.Any<CancellationToken>())
            .Returns(MakeOptimizationRecord(optId));

        var executor = CreateExecutor();
        var ctx = MakeContext(optId);

        // Act — empty cached trials
        await executor.ExecuteAsync(ctx, validationId, [], CancellationToken.None);

        // Assert: completes with empty result, verdict "Red"
        await _validationRepository.Received(1).SaveAsync(
            Arg.Is<ValidationRunRecord>(r =>
                r.Id == validationId &&
                r.CandidatesIn == 0 &&
                r.CandidatesOut == 0 &&
                r.Verdict == "Red" &&
                r.Status == ValidationRunStatus.Completed),
            Arg.Any<CancellationToken>());
    }
}
