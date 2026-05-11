using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Validation;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Application.Validation;

public sealed class RunValidationCommandHandler(
    IRunRepository runRepository,
    IValidationRepository validationRepository,
    IThresholdProfileRepository thresholdProfileRepository,
    ComputeTaskQueue queue) : ICommandHandler<RunValidationCommand, ValidationSubmissionDto>
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

        // Validation requires exactly one Role=Primary. Post-expansion runs are single-primary
        // by construction; this guard catches stale records pre-dating expansion.
        var primaryCount = optimization.DataSubscriptions.Count(s => s.Role == DataFeedRole.Primary);
        if (primaryCount != 1)
            throw new ArgumentException(
                $"Optimization run '{command.OptimizationRunId}' has {primaryCount} Role=Primary " +
                "subscriptions; validation requires exactly one.");

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

        // Everything below may fail — mark placeholder as Failed on error.
        try
        {
        // 5. Enqueue compute task to the queue
        var thresholdProfileJson = JsonSerializer.Serialize(profile, JsonOptions);
        var dssLabel = string.Join(", ", optimization.DataSubscriptions
            .Select(BacktestInputsFormatter.Format));

        var computeTask = new ComputeTask
        {
            JobId = validationId,
            Type = ComputeTaskType.Validation,
            DssIndex = 0,
            RunId = validationId,
            DssLabel = dssLabel,
            ExecutionContext = new ValidationExecutionContext
            {
                OptimizationRunId = command.OptimizationRunId,
                StrategyName = optimization.StrategyName,
                ThresholdProfileName = command.ThresholdProfileName,
                ThresholdProfileJson = thresholdProfileJson,
                Profile = profile,
                StartedAt = startedAt,
            },
        };

        queue.Enqueue(computeTask);
        queue.RegisterJob(validationId, 1, isOptimizationJob: false);

        return new ValidationSubmissionDto(validationId, optimization.TrialCount);
        }
        catch
        {
            await validationRepository.UpdateValidationRunStatusAsync(
                validationId, ValidationRunStatus.Failed, CancellationToken.None);
            throw;
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
