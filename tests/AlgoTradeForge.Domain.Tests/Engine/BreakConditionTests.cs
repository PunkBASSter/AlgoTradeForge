using AlgoTradeForge.Application.Debug;
using AlgoTradeForge.Domain.Engine;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class BreakConditionTests
{
    private static DebugSnapshot Snap(long seq = 1, bool exportable = false, int fills = 0, long tsMs = 1000) =>
        new(seq, tsMs, 0, exportable, fills, 100_000L);

    [Fact]
    public void FromCommand_Next_BreaksOnExportableOnly()
    {
        var condition = BreakCondition.FromCommand(new DebugCommand.Next());
        Assert.False(condition.ShouldBreak(Snap(exportable: false)));
        Assert.True(condition.ShouldBreak(Snap(exportable: true)));
    }

    [Fact]
    public void FromCommand_NextTrade_BreaksOnFillsOnly()
    {
        var condition = BreakCondition.FromCommand(new DebugCommand.NextTrade());
        Assert.False(condition.ShouldBreak(Snap(fills: 0)));
        Assert.True(condition.ShouldBreak(Snap(fills: 1)));
        Assert.True(condition.ShouldBreak(Snap(fills: 3)));
    }

    [Fact]
    public void FromCommand_RunToSequence_BreaksAtOrBeyondTarget()
    {
        var condition = BreakCondition.FromCommand(new DebugCommand.RunToSequence(5));
        Assert.False(condition.ShouldBreak(Snap(seq: 3)));
        Assert.False(condition.ShouldBreak(Snap(seq: 4)));
        Assert.True(condition.ShouldBreak(Snap(seq: 5)));
        Assert.True(condition.ShouldBreak(Snap(seq: 6)));
    }

    [Fact]
    public void FromCommand_RunToTimestamp_BreaksAtOrBeyondTarget()
    {
        var condition = BreakCondition.FromCommand(new DebugCommand.RunToTimestamp(5000));
        Assert.False(condition.ShouldBreak(Snap(tsMs: 4000)));
        Assert.False(condition.ShouldBreak(Snap(tsMs: 4999)));
        Assert.True(condition.ShouldBreak(Snap(tsMs: 5000)));
        Assert.True(condition.ShouldBreak(Snap(tsMs: 6000)));
    }
}
