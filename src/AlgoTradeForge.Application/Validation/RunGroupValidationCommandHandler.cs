using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Validation;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Application.Validation;

public sealed class RunGroupValidationCommandHandler(
    IRunRepository runRepository,
    IValidationRepository validationRepository,
    IThresholdProfileRepository thresholdProfileRepository,
    RunProgressCache progressCache,
    ComputeTaskQueue queue,
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

        // 4. Dedup check under a narrow lock (matching RunGroupOptimizationCommandHandler pattern)
        var groupRunKey = $"val-group:{command.OptimizationGroupId}:{command.ThresholdProfileName}";
        var groupId = Guid.NewGuid();
        using (await progressCache.AcquireRunKeyLockAsync(groupRunKey, ct))
        {
            var existingGroupId = await progressCache.TryGetRunIdByKeyAsync(groupRunKey, ct);
            if (existingGroupId is not null)
            {
                var existingProgress = await progressCache.GetProgressAsync(existingGroupId.Value, ct);
                if (existingProgress is not null)
                {
                    logger.LogInformation(
                        "Group validation dedup hit: existing group {GroupId} for key {RunKey}",
                        existingGroupId.Value, groupRunKey);
                    return new ValidationGroupSubmissionDto
                    {
                        GroupId = existingGroupId.Value,
                        Runs = [],
                    };
                }

                await progressCache.RemoveRunKeyAsync(groupRunKey, ct);
            }

            await progressCache.SetProgressAsync(groupId, 0, 0, ct);
            await progressCache.SetRunKeyAsync(groupRunKey, groupId, ct);
        }

        // Everything below runs outside the lock. On failure, clean up the reservation.
        try
        {
        // 5. Create and insert the validation group record
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

        // 6. Insert child validation placeholders as Enqueued
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

        // 7. Enqueue compute tasks (one per DSS validation)
        var computeTasks = new List<ComputeTask>(completedRuns.Count);
        for (var i = 0; i < completedRuns.Count; i++)
        {
            var optimizationRun = completedRuns[i];
            var dssLabel = string.Join(", ", optimizationRun.DataSubscriptions
                .Select(BacktestInputsFormatter.Format));

            computeTasks.Add(new ComputeTask
            {
                JobId = groupId,
                Type = ComputeTaskType.Validation,
                DssIndex = optimizationRun.DssIndex,
                RunId = childValidationIds[i],
                DssLabel = dssLabel,
                ExecutionContext = new ValidationExecutionContext
                {
                    OptimizationRunId = optimizationRun.Id,
                    StrategyName = optimizationGroup.StrategyName,
                    ThresholdProfileName = command.ThresholdProfileName,
                    ThresholdProfileJson = thresholdProfileJson,
                    Profile = profile,
                    StartedAt = startedAt,
                    ValidationGroupId = groupId,
                },
            });
        }

        queue.EnqueueRange(computeTasks);
        queue.RegisterJob(groupId, computeTasks.Count, isOptimizationJob: false, groupRunKey);

        logger.LogInformation(
            "Validation group {GroupId} created with {TaskCount} tasks enqueued for optimization group {OptGroupId}",
            groupId, computeTasks.Count, command.OptimizationGroupId);

        return new ValidationGroupSubmissionDto
        {
            GroupId = groupId,
            Runs = childRuns,
        };
        }
        catch
        {
            await progressCache.RemoveProgressAsync(groupId);
            await progressCache.RemoveRunKeyAsync(groupRunKey);
            throw;
        }
    }

}
