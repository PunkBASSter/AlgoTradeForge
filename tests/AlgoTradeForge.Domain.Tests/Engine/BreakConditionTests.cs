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

    [Fact]
    public void OnSignal_IsEventLevel_And_BreaksOnSigOnly()
    {
        var condition = BreakCondition.FromCommand(new DebugCommand.NextSignal());

        Assert.True(condition.IsEventLevel);
        Assert.False(condition.ShouldBreak(Snap())); // never breaks on bar

        Assert.True(condition.ShouldBreakOnEvent("sig", 1));
        Assert.False(condition.ShouldBreakOnEvent("ord.fill", 2));
        Assert.False(condition.ShouldBreakOnEvent("bar", 3));
    }

    [Fact]
    public void OnEventType_IsEventLevel_And_BreaksOnMatchOnly()
    {
        var condition = BreakCondition.FromCommand(new DebugCommand.NextType("ord.fill"));

        Assert.True(condition.IsEventLevel);
        Assert.False(condition.ShouldBreak(Snap()));

        Assert.True(condition.ShouldBreakOnEvent("ord.fill", 1));
        Assert.False(condition.ShouldBreakOnEvent("sig", 2));
        Assert.False(condition.ShouldBreakOnEvent("bar", 3));
    }

    [Fact]
    public void BarLevel_Conditions_Are_Not_EventLevel()
    {
        Assert.False(BreakCondition.FromCommand(new DebugCommand.Continue()).IsEventLevel);
        Assert.False(BreakCondition.FromCommand(new DebugCommand.Next()).IsEventLevel);
        Assert.False(BreakCondition.FromCommand(new DebugCommand.NextBar()).IsEventLevel);
        Assert.False(BreakCondition.FromCommand(new DebugCommand.NextTrade()).IsEventLevel);
        Assert.False(BreakCondition.FromCommand(new DebugCommand.Pause()).IsEventLevel);
        Assert.False(BreakCondition.FromCommand(new DebugCommand.RunToSequence(5)).IsEventLevel);
        Assert.False(BreakCondition.FromCommand(new DebugCommand.RunToTimestamp(5000)).IsEventLevel);
    }

    [Fact]
    public void And_RequiresBothSubConditions_ForEventBreak()
    {
        var onSignal = BreakCondition.FromCommand(new DebugCommand.NextSignal());
        var onFill = BreakCondition.FromCommand(new DebugCommand.NextType("ord.fill"));
        var compound = new BreakCondition.And(onSignal, onFill);

        Assert.True(compound.IsEventLevel);
        // "sig" satisfies left but not right
        Assert.False(compound.ShouldBreakOnEvent("sig", 1));
        // "ord.fill" satisfies right but not left
        Assert.False(compound.ShouldBreakOnEvent("ord.fill", 2));
    }

    [Fact]
    public void And_MixedGranularity_Throws()
    {
        var eventLevel = BreakCondition.FromCommand(new DebugCommand.NextSignal());
        var barLevel = BreakCondition.FromCommand(new DebugCommand.RunToTimestamp(5000));

        var ex = Assert.Throws<ArgumentException>(
            () => new BreakCondition.And(eventLevel, barLevel));
        Assert.Contains("granularit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void And_MixedGranularity_Reverse_Throws()
    {
        var barLevel = BreakCondition.FromCommand(new DebugCommand.NextTrade());
        var eventLevel = BreakCondition.FromCommand(new DebugCommand.NextType("ord.fill"));

        Assert.Throws<ArgumentException>(
            () => new BreakCondition.And(barLevel, eventLevel));
    }

    [Fact]
    public void And_SameGranularity_BarLevel_Succeeds()
    {
        var left = BreakCondition.FromCommand(new DebugCommand.RunToTimestamp(5000));
        var right = BreakCondition.FromCommand(new DebugCommand.NextTrade());
        var compound = new BreakCondition.And(left, right);

        Assert.False(compound.IsEventLevel);
        // Needs both: timestamp reached AND fills > 0
        Assert.False(compound.ShouldBreak(Snap(tsMs: 5000, fills: 0)));
        Assert.False(compound.ShouldBreak(Snap(tsMs: 4000, fills: 1)));
        Assert.True(compound.ShouldBreak(Snap(tsMs: 5000, fills: 1)));
    }

    [Fact]
    public void FromCommand_SetExport_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => BreakCondition.FromCommand(new DebugCommand.SetExport(true)));
    }
}
