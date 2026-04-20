using AlgoTradeForge.Application.Optimization;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public sealed class ComputeTaskQueueTests
{
    private static ComputeTask MakeTask(
        ComputeTaskType type = ComputeTaskType.Optimization,
        int dssIndex = 0,
        Guid? jobId = null)
    {
        return new ComputeTask
        {
            JobId = jobId ?? Guid.NewGuid(),
            Type = type,
            DssIndex = dssIndex,
            RunId = Guid.NewGuid(),
            DssLabel = $"TEST/binance/1h DSS#{dssIndex}",
        };
    }

    [Fact]
    public void Enqueue_adds_to_channel_and_dictionary()
    {
        var queue = new ComputeTaskQueue();
        var task = MakeTask();

        queue.Enqueue(task);

        var snapshot = queue.GetSnapshot();
        Assert.Single(snapshot);
        Assert.Equal(task.Id, snapshot[0].Id);
    }

    [Fact]
    public void EnqueueRange_adds_multiple_tasks()
    {
        var queue = new ComputeTaskQueue();
        var jobId = Guid.NewGuid();
        var tasks = new[]
        {
            MakeTask(ComputeTaskType.Optimization, 0, jobId),
            MakeTask(ComputeTaskType.Validation, 0, jobId),
            MakeTask(ComputeTaskType.Optimization, 1, jobId),
        };

        queue.EnqueueRange(tasks);

        var snapshot = queue.GetSnapshot();
        Assert.Equal(3, snapshot.Count);
    }

    [Fact]
    public void TryCancelTask_sets_pending_to_cancelled()
    {
        var queue = new ComputeTaskQueue();
        var task = MakeTask();
        queue.Enqueue(task);

        var result = queue.TryCancelTask(task.Id, out var cancelled, out _);

        Assert.True(result);
        Assert.NotNull(cancelled);
        Assert.Equal(ComputeTaskStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public void TryCancelTask_returns_false_for_unknown_task()
    {
        var queue = new ComputeTaskQueue();

        var result = queue.TryCancelTask(Guid.NewGuid(), out var task, out _);

        Assert.False(result);
        Assert.Null(task);
    }

    [Fact]
    public void TryCancelTask_returns_false_for_terminal_state()
    {
        var queue = new ComputeTaskQueue();
        var task = MakeTask();
        task.Status = ComputeTaskStatus.Completed;
        queue.Enqueue(task);

        var result = queue.TryCancelTask(task.Id, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryCancelTask_cascades_to_paired_validation()
    {
        var queue = new ComputeTaskQueue();
        var jobId = Guid.NewGuid();
        var opt = MakeTask(ComputeTaskType.Optimization, 0, jobId);
        var val = MakeTask(ComputeTaskType.Validation, 0, jobId);

        queue.EnqueueRange([opt, val]);

        var result = queue.TryCancelTask(opt.Id, out _, out var cascaded);

        Assert.True(result);
        Assert.Single(cascaded);
        Assert.Equal(val.Id, cascaded[0].Id);
        Assert.Equal(ComputeTaskStatus.Cancelled, val.Status);
    }

    [Fact]
    public void TryCancelTask_does_not_cascade_across_dss_indices()
    {
        var queue = new ComputeTaskQueue();
        var jobId = Guid.NewGuid();
        var opt0 = MakeTask(ComputeTaskType.Optimization, 0, jobId);
        var val1 = MakeTask(ComputeTaskType.Validation, 1, jobId); // Different DSS index

        queue.EnqueueRange([opt0, val1]);

        queue.TryCancelTask(opt0.Id, out _, out var cascaded);

        Assert.Empty(cascaded);
        Assert.Equal(ComputeTaskStatus.Pending, val1.Status);
    }

    [Fact]
    public void PurgePending_cancels_all_pending_tasks()
    {
        var queue = new ComputeTaskQueue();
        var tasks = new[] { MakeTask(dssIndex: 0), MakeTask(dssIndex: 1), MakeTask(dssIndex: 2) };
        queue.EnqueueRange(tasks);

        var (count, ids) = queue.PurgePending();

        Assert.Equal(3, count);
        Assert.Equal(3, ids.Count);
        Assert.All(tasks, t => Assert.Equal(ComputeTaskStatus.Cancelled, t.Status));
    }

    [Fact]
    public void PurgePending_does_not_affect_in_progress_task()
    {
        var queue = new ComputeTaskQueue();
        var inProgress = MakeTask(dssIndex: 0);
        inProgress.Status = ComputeTaskStatus.InProgress;
        var pending = MakeTask(dssIndex: 1);

        queue.EnqueueRange([inProgress, pending]);
        queue.ActiveTask = inProgress;

        var (count, _) = queue.PurgePending();

        Assert.Equal(1, count);
        Assert.Equal(ComputeTaskStatus.InProgress, inProgress.Status);
    }

    [Fact]
    public void GetSnapshot_returns_in_progress_first_then_pending_ordered()
    {
        var queue = new ComputeTaskQueue();
        var jobId = Guid.NewGuid();
        var first = MakeTask(dssIndex: 0, jobId: jobId);
        first.Status = ComputeTaskStatus.InProgress;
        var second = MakeTask(dssIndex: 1, jobId: jobId);
        var third = MakeTask(dssIndex: 2, jobId: jobId);

        queue.EnqueueRange([first, second, third]);
        queue.ActiveTask = first;

        var snapshot = queue.GetSnapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(ComputeTaskStatus.InProgress, snapshot[0].Status);
        Assert.Equal(ComputeTaskStatus.Pending, snapshot[1].Status);
        Assert.Equal(ComputeTaskStatus.Pending, snapshot[2].Status);
    }

    [Fact]
    public void GetSnapshot_excludes_terminal_tasks()
    {
        var queue = new ComputeTaskQueue();
        var completed = MakeTask(dssIndex: 0);
        completed.Status = ComputeTaskStatus.Completed;
        var pending = MakeTask(dssIndex: 1);

        queue.EnqueueRange([completed, pending]);

        var snapshot = queue.GetSnapshot();

        Assert.Single(snapshot);
        Assert.Equal(pending.Id, snapshot[0].Id);
    }

    [Fact]
    public void RemoveCompleted_removes_task_from_dictionary()
    {
        var queue = new ComputeTaskQueue();
        var task = MakeTask();
        queue.Enqueue(task);
        task.Status = ComputeTaskStatus.Completed;

        queue.RemoveCompleted(task.Id);

        // Task no longer visible (it was completed + removed)
        var snapshot = queue.GetSnapshot();
        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task Channel_reader_receives_enqueued_tasks()
    {
        var queue = new ComputeTaskQueue();
        var task = MakeTask();

        queue.Enqueue(task);
        queue.Complete();

        var received = new List<ComputeTask>();
        await foreach (var t in queue.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            received.Add(t);

        Assert.Single(received);
        Assert.Equal(task.Id, received[0].Id);
    }

    // ── TryCancelJob ─────────────────────────────────────────────

    [Fact]
    public void TryCancelJob_cancels_all_pending_tasks_for_job()
    {
        var queue = new ComputeTaskQueue();
        var jobId = Guid.NewGuid();
        var opt = MakeTask(ComputeTaskType.Optimization, 0, jobId);
        var val = MakeTask(ComputeTaskType.Validation, 0, jobId);

        queue.EnqueueRange([opt, val]);

        var cancelled = queue.TryCancelJob(jobId);

        Assert.Equal(2, cancelled.Count);
        Assert.All(cancelled, t => Assert.Equal(ComputeTaskStatus.Cancelled, t.Status));
    }

    [Fact]
    public void TryCancelJob_returns_empty_for_unknown_job()
    {
        var queue = new ComputeTaskQueue();
        queue.Enqueue(MakeTask());

        var cancelled = queue.TryCancelJob(Guid.NewGuid());

        Assert.Empty(cancelled);
    }

    [Fact]
    public void TryCancelJob_skips_completed_and_failed_tasks()
    {
        var queue = new ComputeTaskQueue();
        var jobId = Guid.NewGuid();
        var completed = MakeTask(ComputeTaskType.Optimization, 0, jobId);
        completed.Status = ComputeTaskStatus.Completed;
        var failed = MakeTask(ComputeTaskType.Optimization, 1, jobId);
        failed.Status = ComputeTaskStatus.Failed;
        var pending = MakeTask(ComputeTaskType.Optimization, 2, jobId);

        queue.EnqueueRange([completed, failed, pending]);

        var cancelled = queue.TryCancelJob(jobId);

        Assert.Single(cancelled);
        Assert.Equal(pending.Id, cancelled[0].Id);
        Assert.Equal(ComputeTaskStatus.Completed, completed.Status);
        Assert.Equal(ComputeTaskStatus.Failed, failed.Status);
    }

    [Fact]
    public void TryCancelJob_includes_in_progress_task()
    {
        var queue = new ComputeTaskQueue();
        var jobId = Guid.NewGuid();
        var inProgress = MakeTask(ComputeTaskType.Optimization, 0, jobId);
        inProgress.Status = ComputeTaskStatus.InProgress;
        var pending = MakeTask(ComputeTaskType.Validation, 0, jobId);

        queue.EnqueueRange([inProgress, pending]);

        var cancelled = queue.TryCancelJob(jobId);

        Assert.Equal(2, cancelled.Count);
        Assert.All(cancelled, t => Assert.Equal(ComputeTaskStatus.Cancelled, t.Status));
    }

    [Fact]
    public void TryCancelJob_does_not_affect_other_jobs()
    {
        var queue = new ComputeTaskQueue();
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();
        var taskA = MakeTask(dssIndex: 0, jobId: jobA);
        var taskB = MakeTask(dssIndex: 0, jobId: jobB);

        queue.EnqueueRange([taskA, taskB]);

        queue.TryCancelJob(jobA);

        Assert.Equal(ComputeTaskStatus.Cancelled, taskA.Status);
        Assert.Equal(ComputeTaskStatus.Pending, taskB.Status);
    }

    // ── Job tracking ──────────────────────────────────────────

    [Fact]
    public void RegisterJob_and_RecordTaskCompletion_tracks_job_lifecycle()
    {
        var queue = new ComputeTaskQueue();
        var jobId = Guid.NewGuid();
        queue.RegisterJob(jobId, totalTasks: 2, isOptimizationJob: true, groupRunKey: "key");

        var task1 = MakeTask(ComputeTaskType.Optimization, 0, jobId);
        task1.Status = ComputeTaskStatus.Completed;
        var (done1, _, _, _) = queue.RecordTaskCompletion(task1);
        Assert.False(done1);

        var task2 = MakeTask(ComputeTaskType.Validation, 0, jobId);
        task2.Status = ComputeTaskStatus.Completed;
        var (done2, statuses, isOpt, runKey) = queue.RecordTaskCompletion(task2);
        Assert.True(done2);
        Assert.NotNull(statuses);
        Assert.Equal(2, statuses.Count);
        Assert.True(isOpt);
        Assert.Equal("key", runKey);
    }
}
