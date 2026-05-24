using System.Diagnostics;
using System.Text.Json;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Scoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Application.Validation;

/// <summary>
/// Context captured at enqueue time for a single-DSS validation.
/// Stored on ComputeTask.ExecutionContext.
/// </summary>
public sealed record ValidationExecutionContext
{
    public required Guid OptimizationRunId { get; init; }
    public required string StrategyName { get; init; }
    public required string ThresholdProfileName { get; init; }
    public required string ThresholdProfileJson { get; init; }
    public required ValidationThresholdProfile Profile { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public Guid? ValidationGroupId { get; init; }
}

/// <summary>
/// Executes the validation pipeline for a single optimization run.
/// Extracted from RunGroupValidationCommandHandler to be called by ComputeQueueConsumer.
/// </summary>
public sealed class ValidationTaskExecutor(
    IRunRepository runRepository,
    IValidationRepository validationRepository,
    RunProgressCache progressCache,
    ISimulationCacheFileStore cacheFileStore,
    IOptions<SimulationCacheOptions> cacheOptions,
    ILogger<ValidationTaskExecutor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Execute validation for a single optimization run.
    /// When cachedTrials is non-null, uses those directly (skips DB load).
    /// When cachedTrials is null, loads trials from DB (standalone validation path).
    /// </summary>
    public async Task ExecuteAsync(
        ValidationExecutionContext ctx,
        Guid validationId,
        IReadOnlyList<BacktestRunRecord>? cachedTrials,
        CancellationToken ct)
    {
        // 1. Get optimization run metadata (always from DB — lightweight)
        var optimizationRun = await runRepository.GetOptimizationByIdAsync(
            ctx.OptimizationRunId, includeEquityCurves: false, includeTrials: false, ct)
            ?? throw new InvalidOperationException(
                $"Optimization run '{ctx.OptimizationRunId}' not found.");

        // 2. Get trials: from cache or DB
        IReadOnlyList<BacktestRunRecord> trials;
        if (cachedTrials is not null)
        {
            logger.LogInformation(
                "Validation {RunId}: using cached trials ({Count} trials) from optimization {OptId}",
                validationId, cachedTrials.Count, ctx.OptimizationRunId);
            trials = cachedTrials;
        }
        else
        {
            logger.LogInformation(
                "Validation {RunId}: loading trials from DB for optimization {OptId}",
                validationId, ctx.OptimizationRunId);
            var fullOpt = await runRepository.GetOptimizationByIdAsync(
                ctx.OptimizationRunId, includeEquityCurves: false, includeTrials: true, ct)
                ?? throw new InvalidOperationException(
                    $"Optimization run '{ctx.OptimizationRunId}' vanished during validation.");
            trials = fullOpt.Trials;
        }

        var trialsWithCurves = trials
            .Where(t => t.TradePnl.Count > 0)
            .ToList();

        if (trialsWithCurves.Count == 0)
        {
            logger.LogWarning(
                "Validation {RunId}: no trials with trade P&L for optimization {OptId} — completing with empty result",
                validationId, ctx.OptimizationRunId);

            var emptyRecord = new ValidationRunRecord
            {
                Id = validationId,
                OptimizationRunId = ctx.OptimizationRunId,
                StrategyName = ctx.StrategyName,
                StrategyVersion = optimizationRun.StrategyVersion,
                StartedAt = ctx.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = 0,
                Status = ValidationRunStatus.Completed,
                ThresholdProfileName = ctx.ThresholdProfileName,
                ThresholdProfileJson = ctx.ThresholdProfileJson,
                CandidatesIn = 0,
                CandidatesOut = 0,
                CompositeScore = 0,
                Verdict = "Red",
                VerdictSummary = "No candidates with trade data.",
                InvocationCount = 0,
                StageResults = [],
            };

            await validationRepository.SaveAsync(emptyRecord, ct);
            return;
        }

        var invocationCount = await validationRepository.CountByOptimizationIdAsync(ctx.OptimizationRunId, ct);
        var sw = Stopwatch.StartNew();

        // 3. Build simulation cache (with spillover to disk if too large)
        var (cache, cacheFileHandle) = await BuildSimulationCache(trialsWithCurves, validationId, ct);
        using var _cacheCleanup = cacheFileHandle;

        var trialSummaries = SimulationCacheBuilder.BuildTrialSummaries(trialsWithCurves);
        var subscriptionGroupMap = SimulationCacheBuilder.BuildSubscriptionGroupMap(trialsWithCurves);

        // 4. Run pipeline
        var pipeline = new ValidationPipeline();
        var (stageResults, survivors) = pipeline.Execute(
            cache, trialSummaries, ctx.Profile, validationId,
            (current, total) =>
                _ = progressCache.SetProgressAsync(validationId, current, total, CancellationToken.None),
            ct,
            optimizationRun.TotalCombinations,
            subscriptionGroupMap);

        sw.Stop();

        // 5. Compute composite score and verdict
        var candidatesOut = survivors.Count;
        var scoreResult = CompositeScoreCalculator.Calculate(
            stageResults, ctx.Profile, trialsWithCurves.Count, candidatesOut);

        // 6. Save completed record
        var record = new ValidationRunRecord
        {
            Id = validationId,
            OptimizationRunId = ctx.OptimizationRunId,
            StrategyName = ctx.StrategyName,
            StrategyVersion = optimizationRun.StrategyVersion,
            StartedAt = ctx.StartedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = sw.ElapsedMilliseconds,
            Status = ValidationRunStatus.Completed,
            ThresholdProfileName = ctx.ThresholdProfileName,
            ThresholdProfileJson = ctx.ThresholdProfileJson,
            CandidatesIn = trialsWithCurves.Count,
            CandidatesOut = candidatesOut,
            CompositeScore = scoreResult.CompositeScore,
            Verdict = scoreResult.Verdict,
            VerdictSummary = scoreResult.VerdictSummary,
            CategoryScoresJson = JsonSerializer.Serialize(scoreResult.CategoryScores, JsonOptions),
            RejectionsJson = JsonSerializer.Serialize(scoreResult.Rejections, JsonOptions),
            InvocationCount = invocationCount,
            StageResults = stageResults,
        };

        await validationRepository.SaveAsync(record, ct);

        logger.LogInformation(
            "Validation {RunId}: {In} candidates -> {Out} survivors, verdict={Verdict} in {Duration}ms",
            validationId, trialsWithCurves.Count, candidatesOut, scoreResult.Verdict, sw.ElapsedMilliseconds);
    }

    private async Task<(SimulationCache Cache, IDisposable? Cleanup)> BuildSimulationCache(
        List<BacktestRunRecord> trials, Guid validationId, CancellationToken ct)
    {
        var estimatedSize = SimulationCacheBuilder.EstimateSize(trials);
        var options = cacheOptions.Value;

        if (estimatedSize > options.SpilloverThresholdBytes)
        {
            logger.LogInformation(
                "SimulationCache ({Size:F1} MB) exceeds threshold, spilling to disk",
                estimatedSize / (1024.0 * 1024.0));

            var filePath = Path.Combine(options.CacheDirectory, $"cache_{validationId:N}.bin");
            await cacheFileStore.WriteDirect(trials, filePath, ct);
            var spilled = await cacheFileStore.Read(filePath, ct);
            return (spilled, new CacheFileCleanup(filePath));
        }

        return (SimulationCacheBuilder.Build(trials), null);
    }
}
