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
        // 1. Load optimization header only (no trials) — fast
        var optimization = await runRepository.GetOptimizationByIdAsync(command.OptimizationRunId, ct)
            ?? throw new ArgumentException($"Optimization run '{command.OptimizationRunId}' not found.");

        if (optimization.Status != OptimizationRunStatus.Completed)
            throw new ArgumentException(
                $"Optimization run '{command.OptimizationRunId}' has status '{optimization.Status}', expected 'Completed'.");

        // 2. Resolve threshold profile (check repository for custom profiles, fall back to built-in)
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

        // 3. Compute invocation count
        var invocationCount = await validationRepository.CountByOptimizationIdAsync(command.OptimizationRunId, ct) + 1;

        // 4. Insert placeholder — use trial count from optimization record
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
            CandidatesIn = optimization.TrialCount,
            InvocationCount = invocationCount,
            ValidationGroupId = command.ValidationGroupId,
        };
        await validationRepository.InsertPlaceholderAsync(placeholder, ct);

        // 5. Store progress (stage 0 of 8)
        await progressCache.SetProgressAsync(validationId, 0, ValidationPipeline.StageCount, ct);

        // 6. Launch background task — trial loading happens here, not in the request path
        _ = Task.Factory.StartNew(
            () => RunValidationAsync(
                validationId, command.OptimizationRunId, profile, command.ThresholdProfileName,
                optimization.StrategyName, optimization.StrategyVersion,
                startedAt, invocationCount, optimization.TotalCombinations),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        return new ValidationSubmissionDto(validationId, optimization.TrialCount);
    }

    private async Task RunValidationAsync(
        Guid validationId,
        Guid optimizationRunId,
        ValidationThresholdProfile profile,
        string profileName,
        string strategyName,
        string strategyVersion,
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

            // Load trials with trade P&L (no equity curves needed)
            var optimization = await runRepository.GetOptimizationByIdAsync(
                optimizationRunId, includeEquityCurves: false, includeTrials: true, ct)
                ?? throw new InvalidOperationException(
                    $"Optimization run '{optimizationRunId}' vanished during validation.");

            var trials = optimization.Trials
                .Where(t => t.TradePnl.Count > 0)
                .ToList();

            if (trials.Count == 0)
                throw new InvalidOperationException(
                    "No trials with trade P&L found. Re-run the optimization.");

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
            var subscriptionGroupMap = SimulationCacheBuilder.BuildSubscriptionGroupMap(trials);

            // Run pipeline
            var pipeline = new ValidationPipeline();
            var (stageResults, survivors) = pipeline.Execute(
                cache, trialSummaries, profile, validationId,
                (current, total) =>
                    _ = progressCache.SetProgressAsync(validationId, current, total, CancellationToken.None),
                ct,
                totalCombinations,
                subscriptionGroupMap);

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
                profileName, thresholdProfileJson, startedAt, 0, invocationCount, "Run was cancelled by user.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Validation {RunId} failed", validationId);
            await SaveErrorAsync(validationId, optimizationRunId, strategyName, strategyVersion,
                profileName, thresholdProfileJson, startedAt, 0, invocationCount, ex.Message);
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
