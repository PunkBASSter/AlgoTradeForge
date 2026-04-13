using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Application.Validation;

public sealed class RunGroupValidationCommandHandler(
    IRunRepository runRepository,
    IValidationRepository validationRepository,
    ICommandHandler<RunValidationCommand, ValidationSubmissionDto> validationHandler,
    ILogger<RunGroupValidationCommandHandler> logger) : ICommandHandler<RunGroupValidationCommand, ValidationGroupSubmissionDto>
{
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

        // 3. Create and insert the validation group record
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

        // 4. Dispatch per-DSS validation to the existing single-run handler
        var childRuns = new List<ValidationGroupRunDto>(completedRuns.Count);

        foreach (var optimizationRun in completedRuns)
        {
            var childCommand = new RunValidationCommand
            {
                OptimizationRunId = optimizationRun.Id,
                ThresholdProfileName = command.ThresholdProfileName,
                ValidationGroupId = groupId,
            };

            try
            {
                var submission = await validationHandler.HandleAsync(childCommand, CancellationToken.None);
                childRuns.Add(new ValidationGroupRunDto
                {
                    Id = submission.Id,
                    OptimizationRunId = optimizationRun.Id,
                    CandidateCount = submission.CandidateCount,
                });

                logger.LogInformation(
                    "Validation group {GroupId}: dispatched validation {ValidationId} for optimization run {OptRunId} ({Candidates} candidates)",
                    groupId, submission.Id, optimizationRun.Id, submission.CandidateCount);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Validation group {GroupId}: failed to dispatch validation for optimization run {OptRunId}",
                    groupId, optimizationRun.Id);
            }
        }

        if (childRuns.Count == 0)
            throw new ArgumentException(
                $"No validation runs could be launched for optimization group '{command.OptimizationGroupId}'.");

        // 5. Launch background monitor to update group status when all children complete
        _ = Task.Factory.StartNew(
            () => MonitorGroupCompletionAsync(groupId, childRuns.Select(r => r.Id).ToList()),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        logger.LogInformation(
            "Validation group {GroupId} created with {RunCount} child validation runs for optimization group {OptGroupId}",
            groupId, childRuns.Count, command.OptimizationGroupId);

        return new ValidationGroupSubmissionDto
        {
            GroupId = groupId,
            Runs = childRuns,
        };
    }

    private async Task MonitorGroupCompletionAsync(Guid groupId, List<Guid> childValidationIds)
    {
        try
        {
            // Poll until all child validation runs are terminal, with a safety timeout
            // to prevent infinite polling if a child validation hangs without updating status.
            var deadline = DateTimeOffset.UtcNow.AddHours(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));

                var allTerminal = true;
                var statuses = new List<string>(childValidationIds.Count);

                foreach (var childId in childValidationIds)
                {
                    var record = await validationRepository.GetByIdAsync(childId);
                    if (record is null)
                    {
                        statuses.Add(ValidationRunStatus.Failed);
                        continue;
                    }

                    statuses.Add(record.Status);
                    if (record.Status == ValidationRunStatus.InProgress)
                    {
                        allTerminal = false;
                        break;
                    }
                }

                if (!allTerminal)
                    continue;

                // All children are terminal -- compute group status
                var groupStatus = GroupStatusCalculator.Compute(statuses);
                await validationRepository.UpdateValidationGroupStatusAsync(
                    groupId, groupStatus, DateTimeOffset.UtcNow);

                logger.LogInformation(
                    "Validation group {GroupId} completed with status {Status}",
                    groupId, groupStatus);
                return;
            }

            // Deadline reached — mark as failed so the group doesn't stay InProgress forever
            logger.LogWarning(
                "Validation group {GroupId} monitor timed out after 2 hours — marking as Failed",
                groupId);
            await validationRepository.UpdateValidationGroupStatusAsync(
                groupId, ValidationGroupStatus.Failed, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Validation group {GroupId} monitor failed", groupId);
            try
            {
                await validationRepository.UpdateValidationGroupStatusAsync(
                    groupId, ValidationGroupStatus.Failed, DateTimeOffset.UtcNow);
            }
            catch (Exception inner)
            {
                logger.LogWarning(inner,
                    "Failed to update validation group {GroupId} status to Failed", groupId);
            }
        }
    }
}
