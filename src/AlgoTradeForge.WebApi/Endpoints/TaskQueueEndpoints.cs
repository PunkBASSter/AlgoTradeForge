using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Endpoints;

public static class TaskQueueEndpoints
{
    public static void MapTaskQueueEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/queue").WithTags("TaskQueue");

        group.MapGet("/", GetQueueSnapshot);
        group.MapPost("/{taskId:guid}/cancel", CancelTask);
        group.MapPost("/purge", PurgePending);
    }

    private static async Task<IResult> GetQueueSnapshot(
        ComputeTaskQueue queue,
        RunProgressCache progressCache,
        CancellationToken ct)
    {
        var snapshot = queue.GetSnapshot();

        var items = new List<TaskQueueItemResponse>(snapshot.Count);
        Guid? inProgressTaskId = null;

        foreach (var task in snapshot)
        {
            TaskProgressDto? progress = null;
            if (task.Status == ComputeTaskStatus.InProgress)
            {
                inProgressTaskId = task.Id;
                var p = await progressCache.GetProgressAsync(task.RunId, ct);
                if (p is not null)
                    progress = new TaskProgressDto { Processed = p.Value.Processed, Total = p.Value.Total };
            }

            items.Add(new TaskQueueItemResponse
            {
                Id = task.Id,
                JobId = task.JobId,
                Type = task.Type.ToString(),
                DssIndex = task.DssIndex,
                DssLabel = task.DssLabel,
                RunId = task.RunId,
                Status = task.Status.ToString(),
                EnqueuedAt = task.EnqueuedAt,
                Progress = progress,
            });
        }

        var pendingCount = items.Count(i => i.Status == "Pending");

        return Results.Ok(new TaskQueueSnapshotResponse
        {
            ActiveTasks = items,
            PendingCount = pendingCount,
            InProgressTask = inProgressTaskId,
        });
    }

    private static IResult CancelTask(
        Guid taskId,
        ComputeTaskQueue queue,
        IRunCancellationRegistry cancellationRegistry)
    {
        if (!queue.TryCancelTask(taskId, out var task, out var cascaded))
        {
            if (task is null)
                return Results.NotFound(new { message = $"Task {taskId} not found in queue." });

            return Results.Conflict(new { message = $"Task {taskId} is already in terminal state '{task.Status}'." });
        }

        // If the task was in-progress, signal its CancellationTokenSource
        // so the executor stops promptly. For pending tasks this is a harmless
        // no-op (no CTS registered in the registry).
        cancellationRegistry.TryCancel(task!.RunId);

        return Results.Ok(new CancelTaskResponse
        {
            TaskId = taskId,
            Status = "Cancelled",
            CascadeCancelled = cascaded.Select(c => c.Id).ToList(),
        });
    }

    private static IResult PurgePending(ComputeTaskQueue queue)
    {
        var (purgedCount, purgedIds) = queue.PurgePending();

        return Results.Ok(new PurgeResponse
        {
            PurgedCount = purgedCount,
            PurgedTaskIds = purgedIds,
        });
    }
}
