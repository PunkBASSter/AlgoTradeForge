using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public static class GroupStatusCalculator
{
    public static string Compute(IReadOnlyList<string> childStatuses)
    {
        if (childStatuses.Count == 0)
            return OptimizationGroupStatus.Completed;

        var hasInProgress = false;
        var hasCompleted = false;
        var hasFailed = false;
        var hasCancelled = false;

        foreach (var status in childStatuses)
        {
            switch (status)
            {
                case OptimizationRunStatus.InProgress:
                    hasInProgress = true;
                    break;
                case OptimizationRunStatus.Completed:
                    hasCompleted = true;
                    break;
                case OptimizationRunStatus.Failed:
                    hasFailed = true;
                    break;
                case OptimizationRunStatus.Cancelled:
                    hasCancelled = true;
                    break;
            }
        }

        if (hasInProgress)
            return OptimizationGroupStatus.InProgress;

        if (hasCompleted && !hasFailed && !hasCancelled)
            return OptimizationGroupStatus.Completed;

        if (hasCompleted)
            return OptimizationGroupStatus.PartiallyCompleted;

        if (hasFailed)
            return OptimizationGroupStatus.Failed;

        return OptimizationGroupStatus.Cancelled;
    }
}
