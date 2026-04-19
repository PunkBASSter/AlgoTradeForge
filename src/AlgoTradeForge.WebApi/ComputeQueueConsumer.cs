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
    GeneticOptimizationTaskExecutor geneticExecutor,
    ValidationTaskExecutor validationExecutor,
    OptimizationSetupHelper setupHelper,
    RunProgressCache progressCache,
    IRunCancellationRegistry cancellationRegistry,
    IRunRepository runRepository,
    IValidationRepository validationRepository,
    ILogger<ComputeQueueConsumer> logger) : BackgroundService
{
    /// <summary>
    /// Per-DSS trial cache: (JobId, DssIndex) → trial results from optimization.
    /// Plain Dictionary is safe here: the channel has <c>SingleReader = true</c>,
    /// so exactly one task executes at a time — no concurrent access to this field.
    /// </summary>
    private readonly Dictionary<(Guid JobId, int DssIndex), IReadOnlyList<BacktestRunRecord>> _trialCache = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ComputeQueueConsumer started — processing tasks sequentially");
        logger.LogWarning(
            "Compute queue is ephemeral. Any tasks from a prior application instance were lost. " +
            "Previously in-progress runs may show stale InProgress status until resubmitted");

        try
        {
            await foreach (var task in queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (task.Status == ComputeTaskStatus.Cancelled)
                {
                    logger.LogInformation("Skipping cancelled task {TaskId} ({Type} DSS[{Dss}])",
                        task.Id, task.Type, task.DssIndex);

                    try { await UpdateRunStatusAsync(task); }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Failed to update DB status for cancelled task {TaskId}", task.Id);
                    }

                    queue.RemoveCompleted(task.Id);
                    await TryFinalizeJobAsync(task);
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

                    if (task.Status != ComputeTaskStatus.Cancelled)
                    {
                        task.Status = ComputeTaskStatus.Completed;
                        logger.LogInformation("Task {TaskId} completed successfully", task.Id);
                    }
                    else
                    {
                        logger.LogInformation(
                            "Task {TaskId} finished execution but was already cancelled", task.Id);
                    }
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
                    task.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                    logger.LogError(ex, "Task {TaskId} failed", task.Id);
                    CascadeCancelValidation(task);
                }
                finally
                {
                    cancellationRegistry.Remove(task.RunId);
                    queue.ActiveTask = null;
                    await progressCache.RemoveProgressAsync(task.RunId);

                    // Update per-DSS run record for non-successful outcomes
                    // (Completed status is persisted inside the executor methods)
                    if (task.Status is ComputeTaskStatus.Cancelled or ComputeTaskStatus.Failed)
                    {
                        try { await UpdateRunStatusAsync(task); }
                        catch (Exception ex)
                        {
                            logger.LogError(ex,
                                "Failed to update DB status to {Status} for task {TaskId}",
                                task.Status, task.Id);
                        }
                    }

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
        switch (task.ExecutionContext)
        {
            case GeneticExecutionContext gc:
                await ExecuteGeneticOptimizationAsync(task, gc, ct);
                break;
            case OptimizationExecutionContext oc:
                await ExecuteBruteForceOptimizationAsync(task, oc, ct);
                break;
            default:
                throw new InvalidOperationException(
                    $"Task {task.Id} has unrecognized execution context: {task.ExecutionContext?.GetType().Name}");
        }
    }

    private async Task ExecuteBruteForceOptimizationAsync(
        ComputeTask task, OptimizationExecutionContext ctx, CancellationToken ct)
    {
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

        FireAndForgetPersist(task.RunId, record, result.Trials.Count);
    }

    private async Task ExecuteGeneticOptimizationAsync(
        ComputeTask task, GeneticExecutionContext ctx, CancellationToken ct)
    {
        await runRepository.UpdateOptimizationRunStatusAsync(
            task.RunId, OptimizationRunStatus.InProgress, CancellationToken.None);
        await progressCache.SetProgressAsync(
            task.RunId, 0, ctx.GaConfig.MaxEvaluations, CancellationToken.None);

        var result = await geneticExecutor.ExecuteAsync(
            ctx, task.RunId, task.DssIndex, ct);

        _trialCache[(task.JobId, task.DssIndex)] = result.Trials;

        var record = new OptimizationRunRecord
        {
            Id = task.RunId,
            StrategyName = ctx.StrategyName,
            StrategyVersion = result.StrategyVersion ?? "0",
            StartedAt = ctx.StartedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = result.DurationMs,
            TotalCombinations = result.TotalEvaluations,
            SortBy = Fitness,
            DataSubscriptions = ctx.SubscriptionDtos,
            BacktestSettings = ctx.BacktestSettings,
            MaxParallelism = ctx.MaxParallelism,
            Trials = result.Trials,
            FailedTrialDetails = result.FailedTrialDetails,
            FilteredTrials = result.FilteredTrials,
            FailedTrials = result.FailedTrials,
            OptimizationMethod = "Genetic",
            GenerationsCompleted = result.GenerationsCompleted,
        };

        FireAndForgetPersist(task.RunId, record, result.Trials.Count);
    }

    private void FireAndForgetPersist(Guid runId, OptimizationRunRecord record, int trialCount)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await setupHelper.SaveOptimizationAsync(record);
                logger.LogInformation(
                    "Persisted optimization results for run {RunId} ({Trials} trials)",
                    runId, trialCount);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist optimization results for run {RunId}", runId);
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

        // Cancel pending validation tasks paired with this failed/cancelled optimization.
        // We iterate the snapshot directly instead of calling TryCancelTask (which would
        // re-cancel the already-terminal optimization task and apply generic error messages).
        var errorMessage = failedTask.Status == ComputeTaskStatus.Cancelled
            ? "Optimization was cancelled."
            : $"Optimization failed: {failedTask.ErrorMessage}";

        var snapshot = queue.GetSnapshot();
        foreach (var pending in snapshot)
        {
            if (pending.Type == ComputeTaskType.Validation
                && pending.JobId == failedTask.JobId
                && pending.DssIndex == failedTask.DssIndex
                && pending.Status == ComputeTaskStatus.Pending)
            {
                pending.Status = ComputeTaskStatus.Cancelled;
                pending.ErrorMessage = errorMessage;

                logger.LogInformation(
                    "Cascade-cancelled validation task {TaskId} (DSS[{Dss}]) due to optimization {Status}",
                    pending.Id, pending.DssIndex, failedTask.Status);
            }
        }

        // Clean up trial cache for this DSS
        _trialCache.Remove((failedTask.JobId, failedTask.DssIndex));
    }

    /// <summary>
    /// Transition the per-DSS run record in the DB to its terminal status
    /// (Cancelled or Failed). Completed runs are persisted by the executor.
    /// </summary>
    private async Task UpdateRunStatusAsync(ComputeTask task)
    {
        var status = task.Status switch
        {
            ComputeTaskStatus.Cancelled => task.Type == ComputeTaskType.Optimization
                ? OptimizationRunStatus.Cancelled : ValidationRunStatus.Cancelled,
            ComputeTaskStatus.Failed => task.Type == ComputeTaskType.Optimization
                ? OptimizationRunStatus.Failed : ValidationRunStatus.Failed,
            _ => throw new ArgumentException(
                $"Unexpected status {task.Status} for DB update", nameof(task)),
        };

        switch (task.Type)
        {
            case ComputeTaskType.Optimization:
                await runRepository.UpdateOptimizationRunStatusAsync(
                    task.RunId, status, CancellationToken.None);
                break;
            case ComputeTaskType.Validation:
                await validationRepository.UpdateValidationRunStatusAsync(
                    task.RunId, status, CancellationToken.None);
                break;
        }
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
                await progressCache.RemoveProgressAsync(completedTask.JobId);
                if (groupRunKey is not null)
                    await progressCache.RemoveRunKeyAsync(groupRunKey);
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
