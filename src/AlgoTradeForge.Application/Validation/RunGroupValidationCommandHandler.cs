using System.Diagnostics;
using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Application.Validation;

public sealed class RunGroupValidationCommandHandler(
    IRunRepository runRepository,
    IValidationRepository validationRepository,
    IThresholdProfileRepository thresholdProfileRepository,
    RunProgressCache progressCache,
    IRunCancellationRegistry cancellationRegistry,
    ISimulationCacheFileStore cacheFileStore,
    IOptions<SimulationCacheOptions> cacheOptions,
    ILogger<RunGroupValidationCommandHandler> logger) : ICommandHandler<RunGroupValidationCommand, ValidationGroupSubmissionDto>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ValidationGroupSubmissionDto> HandleAsync(
        RunGroupValidationCommand command, CancellationToken ct = default)
    {
        // 1. Load the optimization group with child runs
        var optimizationGroup = await runRepository.GetOptimizationGroupByIdAsync(command.OptimizationGroupId, ct)
            ?? throw new ArgumentException($"Optimization group '{command.OptimizationGroupId}' not found.");

        // 2. Validate at least one child run is Completed
        var completedRuns = optimizationGroup.Runs
            .Where(r => r.Status == OptimizationRunStatus.Completed)
            .ToList();

        if (completedRuns.Count == 0)
            throw new ArgumentException(
                $"Optimization group '{command.OptimizationGroupId}' has no completed child runs.");

        // 3. Resolve threshold profile
        ValidationThresholdProfile profile;
        var customRecord = await thresholdProfileRepository.GetByNameAsync(command.ThresholdProfileName, ct);
        if (customRecord is not null)
        {
            profile = JsonSerializer.Deserialize<ValidationThresholdProfile>(
                customRecord.ProfileJson, JsonOptions)
                ?? throw new ArgumentException($"Invalid profile JSON for '{command.ThresholdProfileName}'.");
        }
        else
        {
            profile = ValidationThresholdProfile.GetByName(command.ThresholdProfileName);
        }

        var thresholdProfileJson = JsonSerializer.Serialize(profile, JsonOptions);

        // 4. Create and insert the validation group record
        var groupId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;

        var groupRecord = new ValidationGroupRecord
        {
            Id = groupId,
            OptimizationGroupId = command.OptimizationGroupId,
            StrategyName = optimizationGroup.StrategyName,
            ThresholdProfileName = command.ThresholdProfileName,
            StartedAt = startedAt,
            TotalRuns = completedRuns.Count,
            Status = ValidationGroupStatus.InProgress,
        };
        await validationRepository.InsertValidationGroupAsync(groupRecord, ct);

        // 5. Insert child validation placeholders as Enqueued (no progress cache yet)
        var childRuns = new List<ValidationGroupRunDto>(completedRuns.Count);
        var childValidationIds = new Guid[completedRuns.Count];

        for (var i = 0; i < completedRuns.Count; i++)
        {
            var optimizationRun = completedRuns[i];

            // Count how many previous validations exist for this optimization run
            var invocationCount = await validationRepository.CountByOptimizationIdAsync(optimizationRun.Id, ct) + 1;

            var validationId = Guid.NewGuid();
            childValidationIds[i] = validationId;

            var placeholder = new ValidationRunRecord
            {
                Id = validationId,
                OptimizationRunId = optimizationRun.Id,
                StrategyName = optimizationGroup.StrategyName,
                StrategyVersion = optimizationRun.StrategyVersion,
                StartedAt = startedAt,
                Status = ValidationRunStatus.Enqueued,
                ThresholdProfileName = command.ThresholdProfileName,
                ThresholdProfileJson = thresholdProfileJson,
                CandidatesIn = 0, // updated when the run starts and trials are loaded
                InvocationCount = invocationCount,
                ValidationGroupId = groupId,
            };
            await validationRepository.InsertPlaceholderAsync(placeholder, ct);

            childRuns.Add(new ValidationGroupRunDto
            {
                Id = validationId,
                OptimizationRunId = optimizationRun.Id,
                CandidateCount = 0,
            });
        }

        // 6. Launch ONE background task for sequential execution
        _ = Task.Factory.StartNew(
            () => RunGroupSequentialAsync(
                groupId, completedRuns, childValidationIds,
                profile, command.ThresholdProfileName, thresholdProfileJson,
                optimizationGroup.StrategyName, startedAt),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        logger.LogInformation(
            "Validation group {GroupId} created with {RunCount} child validation runs (enqueued) for optimization group {OptGroupId}",
            groupId, childRuns.Count, command.OptimizationGroupId);

        return new ValidationGroupSubmissionDto
        {
            GroupId = groupId,
            Runs = childRuns,
        };
    }

    private async Task RunGroupSequentialAsync(
        Guid groupId,
        List<OptimizationRunRecord> completedOptRuns,
        Guid[] childValidationIds,
        ValidationThresholdProfile profile,
        string profileName,
        string thresholdProfileJson,
        string strategyName,
        DateTimeOffset startedAt)
    {
        using var groupCts = new CancellationTokenSource(TimeSpan.FromHours(2));
        cancellationRegistry.Register(groupId, groupCts);
        var groupCt = groupCts.Token;

        var childStatuses = new List<string>(completedOptRuns.Count);

        try
        {
            for (var i = 0; i < completedOptRuns.Count; i++)
            {
                groupCt.ThrowIfCancellationRequested();

                var optimizationRun = completedOptRuns[i];
                var validationId = childValidationIds[i];

                // Transition: Enqueued → InProgress
                await validationRepository.UpdateValidationRunStatusAsync(
                    validationId, ValidationRunStatus.InProgress, CancellationToken.None);
                await progressCache.SetProgressAsync(
                    validationId, 0, ValidationPipeline.StageCount, CancellationToken.None);

                // Per-run CTS linked to group
                using var runCts = CancellationTokenSource.CreateLinkedTokenSource(groupCt);
                cancellationRegistry.Register(validationId, runCts);

                try
                {
                    await RunSingleValidationAsync(
                        validationId, optimizationRun, profile, profileName,
                        thresholdProfileJson, strategyName, startedAt, runCts.Token);

                    childStatuses.Add(ValidationRunStatus.Completed);
                }
                catch (OperationCanceledException) when (!groupCt.IsCancellationRequested)
                {
                    // Individual run cancelled — save and continue
                    logger.LogInformation(
                        "Validation group {GroupId}: run {RunId} was cancelled individually",
                        groupId, validationId);

                    await SaveErrorAsync(validationId, optimizationRun, strategyName,
                        profileName, thresholdProfileJson, startedAt,
                        "Run was cancelled by user.");

                    childStatuses.Add(ValidationRunStatus.Cancelled);
                }
                catch (OperationCanceledException)
                {
                    // Group-level cancellation — re-throw
                    throw;
                }
                catch (Exception ex)
                {
                    // Run failed — save error, continue to next
                    logger.LogError(ex,
                        "Validation group {GroupId}: run {RunId} failed",
                        groupId, validationId);

                    await SaveErrorAsync(validationId, optimizationRun, strategyName,
                        profileName, thresholdProfileJson, startedAt,
                        ex.Message);

                    childStatuses.Add(ValidationRunStatus.Failed);
                }
                finally
                {
                    await progressCache.RemoveProgressAsync(validationId);
                    cancellationRegistry.Remove(validationId);
                }
            }

            // All children processed
            var groupStatus = GroupStatusCalculator.Compute(childStatuses);
            await validationRepository.UpdateValidationGroupStatusAsync(
                groupId, groupStatus, DateTimeOffset.UtcNow, CancellationToken.None);

            logger.LogInformation(
                "Validation group {GroupId} completed with status {Status}",
                groupId, groupStatus);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Validation group {GroupId} was cancelled", groupId);

            // Mark remaining enqueued runs as cancelled
            for (var i = childStatuses.Count; i < completedOptRuns.Count; i++)
            {
                await SaveErrorAsync(childValidationIds[i], completedOptRuns[i], strategyName,
                    profileName, thresholdProfileJson, startedAt,
                    "Run was cancelled by user.");
            }

            await validationRepository.UpdateValidationGroupStatusAsync(
                groupId, ValidationGroupStatus.Cancelled, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Validation group {GroupId} failed", groupId);

            for (var i = childStatuses.Count; i < completedOptRuns.Count; i++)
            {
                await SaveErrorAsync(childValidationIds[i], completedOptRuns[i], strategyName,
                    profileName, thresholdProfileJson, startedAt,
                    ex.Message);
            }

            await validationRepository.UpdateValidationGroupStatusAsync(
                groupId, ValidationGroupStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        finally
        {
            cancellationRegistry.Remove(groupId);
        }
    }

    /// <summary>Run the validation pipeline for a single optimization run (inline).</summary>
    private async Task RunSingleValidationAsync(
        Guid validationId,
        OptimizationRunRecord optimizationRun,
        ValidationThresholdProfile profile,
        string profileName,
        string thresholdProfileJson,
        string strategyName,
        DateTimeOffset startedAt,
        CancellationToken ct)
    {
        // Load trials with trade P&L (no equity curves needed)
        var optimization = await runRepository.GetOptimizationByIdAsync(
            optimizationRun.Id, includeEquityCurves: false, includeTrials: true, ct)
            ?? throw new InvalidOperationException(
                $"Optimization run '{optimizationRun.Id}' vanished during validation.");

        var trialsWithCurves = optimization.Trials
            .Where(t => t.TradePnl.Count > 0)
            .ToList();

        if (trialsWithCurves.Count == 0)
        {
            logger.LogWarning(
                "Validation {RunId}: no trials with trade P&L for optimization {OptId}",
                validationId, optimizationRun.Id);
            throw new InvalidOperationException("No trials with trade P&L found.");
        }

        var invocationCount = await validationRepository.CountByOptimizationIdAsync(optimizationRun.Id, ct);

        var sw = Stopwatch.StartNew();

        // Build simulation cache (with spillover to disk if too large)
        var estimatedSize = SimulationCacheBuilder.EstimateSize(trialsWithCurves);
        SimulationCache cache;
        using var cacheFileHandle = BuildSimulationCache(
            trialsWithCurves, validationId, out cache);

        var trialSummaries = SimulationCacheBuilder.BuildTrialSummaries(trialsWithCurves);
        var subscriptionGroupMap = SimulationCacheBuilder.BuildSubscriptionGroupMap(trialsWithCurves);

        // Run pipeline
        var pipeline = new ValidationPipeline();
        var (stageResults, survivors) = pipeline.Execute(
            cache, trialSummaries, profile, validationId,
            (current, total) =>
                _ = progressCache.SetProgressAsync(validationId, current, total, CancellationToken.None),
            ct,
            optimization.TotalCombinations,
            subscriptionGroupMap);

        sw.Stop();

        // Compute composite score and verdict
        var candidatesOut = survivors.Count;
        var scoreResult = CompositeScoreCalculator.Calculate(
            stageResults, profile, trialsWithCurves.Count, candidatesOut);

        // Save completed record
        var record = new ValidationRunRecord
        {
            Id = validationId,
            OptimizationRunId = optimizationRun.Id,
            StrategyName = strategyName,
            StrategyVersion = optimization.StrategyVersion,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = sw.ElapsedMilliseconds,
            Status = ValidationRunStatus.Completed,
            ThresholdProfileName = profileName,
            ThresholdProfileJson = thresholdProfileJson,
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

    private IDisposable? BuildSimulationCache(
        List<BacktestRunRecord> trials, Guid validationId, out SimulationCache cache)
    {
        var estimatedSize = SimulationCacheBuilder.EstimateSize(trials);
        var options = cacheOptions.Value;

        if (estimatedSize > options.SpilloverThresholdBytes)
        {
            logger.LogInformation(
                "SimulationCache ({Size:F1} MB) exceeds threshold, spilling to disk",
                estimatedSize / (1024.0 * 1024.0));

            var filePath = Path.Combine(options.CacheDirectory, $"cache_{validationId:N}.bin");
            cacheFileStore.WriteDirect(trials, filePath);
            cache = cacheFileStore.Read(filePath);
            return new CacheFileCleanup(filePath);
        }

        cache = SimulationCacheBuilder.Build(trials);
        return null;
    }

    private async Task SaveErrorAsync(
        Guid validationId,
        OptimizationRunRecord optimizationRun,
        string strategyName,
        string profileName,
        string thresholdProfileJson,
        DateTimeOffset startedAt,
        string errorMessage)
    {
        try
        {
            var record = new ValidationRunRecord
            {
                Id = validationId,
                OptimizationRunId = optimizationRun.Id,
                StrategyName = strategyName,
                StrategyVersion = optimizationRun.StrategyVersion,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
                Status = errorMessage == "Run was cancelled by user."
                    ? ValidationRunStatus.Cancelled
                    : ValidationRunStatus.Failed,
                ThresholdProfileName = profileName,
                ThresholdProfileJson = thresholdProfileJson,
                CandidatesIn = 0,
                InvocationCount = 0,
                ErrorMessage = errorMessage,
            };
            await validationRepository.SaveAsync(record);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to persist error record for validation {RunId}", validationId);
        }
    }
}
