using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public sealed class GroupStatusCalculatorTests
{
    [Fact]
    public void AllCompleted_ReturnsCompleted()
    {
        var statuses = new[] { OptimizationRunStatus.Completed, OptimizationRunStatus.Completed, OptimizationRunStatus.Completed };
        Assert.Equal(OptimizationGroupStatus.Completed, GroupStatusCalculator.Compute(statuses));
    }

    [Fact]
    public void AnyInProgress_ReturnsInProgress()
    {
        var statuses = new[] { OptimizationRunStatus.Completed, OptimizationRunStatus.InProgress, OptimizationRunStatus.Failed };
        Assert.Equal(OptimizationGroupStatus.InProgress, GroupStatusCalculator.Compute(statuses));
    }

    [Fact]
    public void MixedCompletedAndFailed_ReturnsPartiallyCompleted()
    {
        var statuses = new[] { OptimizationRunStatus.Completed, OptimizationRunStatus.Failed };
        Assert.Equal(OptimizationGroupStatus.PartiallyCompleted, GroupStatusCalculator.Compute(statuses));
    }

    [Fact]
    public void MixedCompletedAndCancelled_ReturnsPartiallyCompleted()
    {
        var statuses = new[] { OptimizationRunStatus.Completed, OptimizationRunStatus.Cancelled };
        Assert.Equal(OptimizationGroupStatus.PartiallyCompleted, GroupStatusCalculator.Compute(statuses));
    }

    [Fact]
    public void AllFailed_ReturnsFailed()
    {
        var statuses = new[] { OptimizationRunStatus.Failed, OptimizationRunStatus.Failed };
        Assert.Equal(OptimizationGroupStatus.Failed, GroupStatusCalculator.Compute(statuses));
    }

    [Fact]
    public void AllCancelled_ReturnsCancelled()
    {
        var statuses = new[] { OptimizationRunStatus.Cancelled, OptimizationRunStatus.Cancelled };
        Assert.Equal(OptimizationGroupStatus.Cancelled, GroupStatusCalculator.Compute(statuses));
    }

    [Fact]
    public void MixedFailedAndCancelled_ReturnsFailed()
    {
        var statuses = new[] { OptimizationRunStatus.Failed, OptimizationRunStatus.Cancelled };
        Assert.Equal(OptimizationGroupStatus.Failed, GroupStatusCalculator.Compute(statuses));
    }

    [Fact]
    public void Empty_ReturnsCompleted()
    {
        Assert.Equal(OptimizationGroupStatus.Completed, GroupStatusCalculator.Compute([]));
    }

    [Fact]
    public void SingleInProgress_ReturnsInProgress()
    {
        Assert.Equal(OptimizationGroupStatus.InProgress, GroupStatusCalculator.Compute([OptimizationRunStatus.InProgress]));
    }
}
