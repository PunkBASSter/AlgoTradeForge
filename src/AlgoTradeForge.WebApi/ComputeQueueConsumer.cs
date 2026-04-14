using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Validation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static AlgoTradeForge.Domain.Reporting.MetricNames;

namespace AlgoTradeForge.WebApi;

/// <summary>
/// Singleton BackgroundService that consumes compute tasks from the queue,
/// executing one at a time. Manages per-DSS trial cache handoff between
/// optimization and validation tasks, and fire-and-forget DB persistence.
/// </summary>
public sealed class ComputeQueueConsumer(
    ComputeTaskQueue queue,
    OptimizationTaskExecutor optimizationExecutor,
    ValidationTaskExecutor validationExecutor,
    OptimizationSetupHelper setupHelper,
    RunProgressCache progressCache,
    IRunCancellationRegistry cancellationRegistry,
    IRunRepository runRepository,
    IValidationRepository validationRepository,
    ILogger<ComputeQueueConsumer> logger) : BackgroundService
{
    // Per-DSS trial cache: (JobId, DssIndex) → trial results from optimization
    private readonly Dictionary<(Guid JobId, int DssIndex), IReadOnlyList<BacktestRunRecord>> _trialCache = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ComputeQueueConsumer started — processing tasks sequentially");

        try
        {
            await foreach (var task in queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (task.Status == ComputeTaskStatus.Cancelled)
                {
                    logger.LogInformation("Skipping cancelled task {TaskId} ({Type} DSS[{Dss}])",
                        task.Id, task.Type, task.DssIndex);
                    queue.RemoveCompleted(task.Id);
                    continue;
                }

                task.Status = ComputeTaskStatus.InProgress;
                queue.ActiveTask = task;

                logger.LogInformation(
                    "Starting task {TaskId}: {Type} DSS[{Dss}] ({Label}) for job {JobId}",
                    task.Id, task.Type, task.DssIndex, task.DssLabel, task.JobId);

                using var taskCts = new CancellationTokenSource();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    taskCts.Token, stoppingToken);
                cancellationRegistry.Register(task.RunId, taskCts);

                try
                {
                    switch (task.Type)
                    {
                        case ComputeTaskType.Optimization:
                            await ExecuteOptimizationAsync(task, linkedCts.Token);
                            break;
                        case ComputeTaskType.Validation:
                            await ExecuteValidationAsync(task, linkedCts.Token);
                            break;
                    }

                    task.Status = ComputeTaskStatus.Completed;
                    logger.LogInformation("Task {TaskId} completed successfully", task.Id);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    task.Status = ComputeTaskStatus.Cancelled;
                    task.ErrorMessage = "Application shutting down.";
                    logger.LogInformation("Task {TaskId} cancelled due to application shutdown", task.Id);
                }
                catch (OperationCanceledException)
                {
                    task.Status = ComputeTaskStatus.Cancelled;
                    task.ErrorMessage = "Cancelled by user.";
                    logger.LogInformation("Task {TaskId} cancelled by user", task.Id);
                    CascadeCancelValidation(task);
                }
                catch (Exception ex)
                {
                    task.Status = ComputeTaskStatus.Failed;
                    task.ErrorMessage = ex.Message;
                    logger.LogError(ex, "Task {TaskId} failed", task.Id);
                    CascadeCancelValidation(task);
                }
                finally
                {
                    cancellationRegistry.Remove(task.RunId);
                    queue.ActiveTask = null;
                    await progressCache.RemoveProgressAsync(task.RunId);
                    queue.RemoveCompleted(task.Id);

                    // Check if all tasks for this job are done → update group status
                    await TryFinalizeJobAsync(task);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("ComputeQueueConsumer stopping");
        }
    }

    private async Task ExecuteOptimizationAsync(ComputeTask task, CancellationToken ct)
    {
        if (task.ExecutionContext is not OptimizationExecutionContext ctx)
            throw new InvalidOperationException($"Task {task.Id} has no OptimizationExecutionContext");

        // Transition DB: Enqueued → InProgress
        await runRepository.UpdateOptimizationRunStatusAsync(
            task.RunId, OptimizationRunStatus.InProgress, CancellationToken.None);
        await progressCache.SetProgressAsync(
            task.RunId, 0, ctx.EstimatedCount, CancellationToken.None);

        var result = await optimizationExecutor.ExecuteAsync(
            ctx, task.RunId, task.DssIndex, ct);

        // Cache trials for potential validation task (same job + DSS)
        _trialCache[(task.JobId, task.DssIndex)] = result.Trials;

        // Persist results — fire-and-forget so next task can start sooner
        // (validation reads from cache, not DB)
        var record = new OptimizationRunRecord
        {
            Id = task.RunId,
            StrategyName = ctx.StrategyName,
            StrategyVersion = result.StrategyVersion ?? "0",
            StartedAt = ctx.StartedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = result.DurationMs,
            TotalCombinations = ctx.EstimatedCount,
            SortBy = Fitness,
            DataSubscriptions = ctx.SubscriptionDtos,
            BacktestSettings = ctx.BacktestSettings,
            MaxParallelism = ctx.MaxParallelism,
            Trials = result.Trials,
            FailedTrialDetails = result.FailedTrialDetails,
            FilteredTrials = result.FilteredTrials,
            FailedTrials = result.FailedTrials,
            OptimizationMethod = ctx.OptimizationMethod,
            GroupId = ctx.GroupId,
            DssIndex = task.DssIndex,
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await setupHelper.SaveOptimizationAsync(record);
                logger.LogInformation(
                    "Persisted optimization results for run {RunId} ({Trials} trials)",
                    task.RunId, result.Trials.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist optimization results for run {RunId}", task.RunId);
            }
        });
    }

    private async Task ExecuteValidationAsync(ComputeTask task, CancellationToken ct)
    {
        if (task.ExecutionContext is not ValidationExecutionContext ctx)
            throw new InvalidOperationException($"Task {task.Id} has no ValidationExecutionContext");

        // Transition DB: Enqueued → InProgress
        await validationRepository.UpdateValidationRunStatusAsync(
            task.RunId, ValidationRunStatus.InProgress, CancellationToken.None);
        await progressCache.SetProgressAsync(
            task.RunId, 0, ValidationPipeline.StageCount, CancellationToken.None);

        // Check for cached trials from preceding optimization
        _trialCache.TryGetValue((task.JobId, task.DssIndex), out var cachedTrials);

        await validationExecutor.ExecuteAsync(ctx, task.RunId, cachedTrials, ct);

        // Release cache for this DSS — no longer needed
        _trialCache.Remove((task.JobId, task.DssIndex));
    }

    private void CascadeCancelValidation(ComputeTask failedTask)
    {
        if (failedTask.Type != ComputeTaskType.Optimization)
            return;

        // Cancel paired pending validation tasks for the same DSS
        if (queue.TryCancelTask(failedTask.Id, out _, out var cascaded))
        {
            // TryCancelTask already handled the task itself; cascaded contains validation tasks
        }

        // Also explicitly search for pending validation tasks
        var snapshot = queue.GetSnapshot();
        foreach (var pending in snapshot)
        {
            if (pending.Type == ComputeTaskType.Validation
                && pending.JobId == failedTask.JobId
                && pending.DssIndex == failedTask.DssIndex
                && pending.Status == ComputeTaskStatus.Pending)
            {
                pending.Status = ComputeTaskStatus.Cancelled;
                pending.ErrorMessage = failedTask.Status == ComputeTaskStatus.Cancelled
                    ? "Optimization was cancelled."
                    : $"Optimization failed: {failedTask.ErrorMessage}";

                logger.LogInformation(
                    "Cascade-cancelled validation task {TaskId} (DSS[{Dss}]) due to optimization {Status}",
                    pending.Id, pending.DssIndex, failedTask.Status);
            }
        }

        // Clean up trial cache for this DSS
        _trialCache.Remove((failedTask.JobId, failedTask.DssIndex));
    }

    private async Task TryFinalizeJobAsync(ComputeTask completedTask)
    {
        var (isComplete, childStatuses, isOptimizationJob, groupRunKey) =
            queue.RecordTaskCompletion(completedTask);

        if (!isComplete || childStatuses is null)
            return;

        var groupStatus = GroupStatusCalculator.Compute(childStatuses);

        try
        {
            if (isOptimizationJob)
            {
                await runRepository.UpdateOptimizationGroupStatusAsync(
                    completedTask.JobId, groupStatus, DateTimeOffset.UtcNow, CancellationToken.None);
                await progressCache.RemoveProgressAsync(completedTask.JobId);
                if (groupRunKey is not null)
                    await progressCache.RemoveRunKeyAsync(groupRunKey);
            }
            else
            {
                await validationRepository.UpdateValidationGroupStatusAsync(
                    completedTask.JobId, groupStatus, DateTimeOffset.UtcNow, CancellationToken.None);
            }

            logger.LogInformation("Job {JobId} finalized with status {Status}",
                completedTask.JobId, groupStatus);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to finalize job {JobId}", completedTask.JobId);
        }
    }
}
