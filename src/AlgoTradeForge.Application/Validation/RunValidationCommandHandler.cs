using System.Diagnostics;
using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Application.Validation;

public sealed class RunValidationCommandHandler(
    IRunRepository runRepository,
    IValidationRepository validationRepository,
    IThresholdProfileRepository thresholdProfileRepository,
    RunProgressCache progressCache,
    IRunCancellationRegistry cancellationRegistry,
    ISimulationCacheFileStore cacheFileStore,
    IOptions<SimulationCacheOptions> cacheOptions,
    ILogger<RunValidationCommandHandler> logger) : ICommandHandler<RunValidationCommand, ValidationSubmissionDto>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ValidationSubmissionDto> HandleAsync(RunValidationCommand command, CancellationToken ct = default)
    {
        // 1. Load the optimization run (with equity curves for simulation cache)
        var optimization = await runRepository.GetOptimizationByIdAsync(command.OptimizationRunId, includeEquityCurves: true, ct)
            ?? throw new ArgumentException($"Optimization run '{command.OptimizationRunId}' not found.");

        if (optimization.Status != OptimizationRunStatus.Completed)
            throw new ArgumentException(
                $"Optimization run '{command.OptimizationRunId}' has status '{optimization.Status}', expected 'Completed'.");

        // 2. Validate trials have equity curves
        var trialsWithCurves = optimization.Trials
            .Where(t => t.EquityCurve.Count > 0)
            .ToList();

        if (trialsWithCurves.Count == 0)
            throw new ArgumentException(
                "No trials with equity curves found. Re-run the optimization with equity curve retention enabled.");

        // 3. Resolve threshold profile (check repository for custom profiles, fall back to built-in)
        ValidationThresholdProfile profile;
        var customRecord = await thresholdProfileRepository.GetByNameAsync(command.ThresholdProfileName, ct);
        if (customRecord is not null)
        {
            profile = System.Text.Json.JsonSerializer.Deserialize<ValidationThresholdProfile>(
                customRecord.ProfileJson, JsonOptions)
                ?? throw new ArgumentException($"Invalid profile JSON for '{command.ThresholdProfileName}'.");
        }
        else
        {
            profile = ValidationThresholdProfile.GetByName(command.ThresholdProfileName);
        }

        // 4. Compute invocation count
        var invocationCount = await validationRepository.CountByOptimizationIdAsync(command.OptimizationRunId, ct) + 1;

        // 5. Insert placeholder
        var validationId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var placeholder = new ValidationRunRecord
        {
            Id = validationId,
            OptimizationRunId = command.OptimizationRunId,
            StrategyName = optimization.StrategyName,
            StrategyVersion = optimization.StrategyVersion,
            StartedAt = startedAt,
            Status = ValidationRunStatus.InProgress,
            ThresholdProfileName = command.ThresholdProfileName,
            ThresholdProfileJson = JsonSerializer.Serialize(profile, JsonOptions),
            CandidatesIn = trialsWithCurves.Count,
            InvocationCount = invocationCount,
        };
        await validationRepository.InsertPlaceholderAsync(placeholder, ct);

        // 6. Store progress (stage 0 of 8)
        await progressCache.SetProgressAsync(validationId, 0, ValidationPipeline.StageCount, ct);

        // 7. Launch background task
        _ = Task.Factory.StartNew(
            () => RunValidationAsync(
                validationId, trialsWithCurves, profile, command.ThresholdProfileName,
                optimization.StrategyName, optimization.StrategyVersion,
                command.OptimizationRunId, startedAt, invocationCount,
                optimization.TotalCombinations),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        return new ValidationSubmissionDto(validationId, trialsWithCurves.Count);
    }

    private async Task RunValidationAsync(
        Guid validationId,
        List<BacktestRunRecord> trials,
        ValidationThresholdProfile profile,
        string profileName,
        string strategyName,
        string strategyVersion,
        Guid optimizationRunId,
        DateTimeOffset startedAt,
        int invocationCount,
        long totalCombinations)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        cancellationRegistry.Register(validationId, cts);
        var ct = cts.Token;

        var thresholdProfileJson = JsonSerializer.Serialize(profile, JsonOptions);
        IDisposable? cacheFileHandle = null;

        try
        {
            var sw = Stopwatch.StartNew();

            // Build simulation cache (with spillover to disk if too large)
            var estimatedSize = SimulationCacheBuilder.EstimateSize(trials);
            SimulationCache cache;
            var options = cacheOptions.Value;

            if (estimatedSize > options.SpilloverThresholdBytes)
            {
                logger.LogInformation(
                    "SimulationCache ({Size:F1} MB) exceeds threshold ({Threshold:F1} MB), spilling to disk",
                    estimatedSize / (1024.0 * 1024.0),
                    options.SpilloverThresholdBytes / (1024.0 * 1024.0));

                // Write directly from trials to binary, then load once
                var filePath = Path.Combine(options.CacheDirectory, $"cache_{validationId:N}.bin");
                cacheFileStore.WriteDirect(trials, filePath);
                cache = cacheFileStore.Read(filePath);
                cacheFileHandle = new CacheFileCleanup(filePath);
            }
            else
            {
                cache = SimulationCacheBuilder.Build(trials);
            }

            var trialSummaries = SimulationCacheBuilder.BuildTrialSummaries(trials);

            // Run pipeline
            var pipeline = new ValidationPipeline();
            var (stageResults, survivors) = pipeline.Execute(
                cache, trialSummaries, profile, validationId,
                (current, total) =>
                    _ = progressCache.SetProgressAsync(validationId, current, total, CancellationToken.None),
                ct,
                totalCombinations);

            sw.Stop();

            // Compute composite score and verdict
            var candidatesOut = survivors.Count;
            var scoreResult = CompositeScoreCalculator.Calculate(
                stageResults, profile, trials.Count, candidatesOut);

            // Save completed record
            var record = new ValidationRunRecord
            {
                Id = validationId,
                OptimizationRunId = optimizationRunId,
                StrategyName = strategyName,
                StrategyVersion = strategyVersion,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = (long)sw.Elapsed.TotalMilliseconds,
                Status = ValidationRunStatus.Completed,
                ThresholdProfileName = profileName,
                ThresholdProfileJson = thresholdProfileJson,
                CandidatesIn = trials.Count,
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
                "Validation {RunId}: {In} candidates → {Out} survivors, verdict={Verdict} in {Duration}ms",
                validationId, trials.Count, candidatesOut, scoreResult.Verdict, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Validation {RunId} was cancelled", validationId);
            await SaveErrorAsync(validationId, optimizationRunId, strategyName, strategyVersion,
                profileName, thresholdProfileJson, startedAt, trials.Count, invocationCount, "Run was cancelled by user.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Validation {RunId} failed", validationId);
            await SaveErrorAsync(validationId, optimizationRunId, strategyName, strategyVersion,
                profileName, thresholdProfileJson, startedAt, trials.Count, invocationCount, ex.Message);
        }
        finally
        {
            cacheFileHandle?.Dispose();
            await progressCache.RemoveProgressAsync(validationId);
            cancellationRegistry.Remove(validationId);
        }
    }

    private async Task SaveErrorAsync(
        Guid validationId, Guid optimizationRunId, string strategyName, string strategyVersion,
        string profileName, string thresholdProfileJson, DateTimeOffset startedAt, int candidatesIn,
        int invocationCount, string errorMessage)
    {
        try
        {
            var record = new ValidationRunRecord
            {
                Id = validationId,
                OptimizationRunId = optimizationRunId,
                StrategyName = strategyName,
                StrategyVersion = strategyVersion,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
                Status = errorMessage == "Run was cancelled by user."
                    ? ValidationRunStatus.Cancelled
                    : ValidationRunStatus.Failed,
                ThresholdProfileName = profileName,
                ThresholdProfileJson = thresholdProfileJson,
                CandidatesIn = candidatesIn,
                InvocationCount = invocationCount,
                ErrorMessage = errorMessage,
            };
            await validationRepository.SaveAsync(record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist error record for validation {RunId}", validationId);
        }
    }
}

/// <summary>
/// Disposable that deletes a cache file when disposed.
/// Used to ensure spillover cache files are cleaned up after validation completes.
/// </summary>
internal sealed class CacheFileCleanup(string filePath) : IDisposable
{
    public void Dispose()
    {
        try { File.Delete(filePath); }
        catch { /* best-effort cleanup */ }
    }
}
