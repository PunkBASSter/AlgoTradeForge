using AlgoTradeForge.Domain.Engine;

namespace AlgoTradeForge.Application.Debug;

/// <summary>
/// Break condition evaluated at each bar boundary to decide whether the engine should pause.
/// </summary>
internal abstract record BreakCondition
{
    private BreakCondition() { }

    public abstract bool ShouldBreak(DebugSnapshot snapshot);

    public static BreakCondition FromCommand(DebugCommand command) => command switch
    {
        DebugCommand.Continue => Never.Instance,
        DebugCommand.Next => new OnExportableBar(),
        DebugCommand.NextBar => Always.Instance,
        DebugCommand.NextTrade => new OnFillBar(),
        DebugCommand.RunToSequence(var sq) => new AtSequence(sq),
        DebugCommand.RunToTimestamp(var ms) => new AtTimestamp(ms),
        DebugCommand.Pause => Always.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unhandled debug command type.")
    };

    /// <summary>Always break — used for NextBar and Pause.</summary>
    internal sealed record Always : BreakCondition
    {
        internal static readonly Always Instance = new();
        public override bool ShouldBreak(DebugSnapshot snapshot) => true;
    }

    /// <summary>Never break — used for Continue.</summary>
    internal sealed record Never : BreakCondition
    {
        internal static readonly Never Instance = new();
        public override bool ShouldBreak(DebugSnapshot snapshot) => false;
    }

    /// <summary>Break on bars from exportable subscriptions only.</summary>
    internal sealed record OnExportableBar : BreakCondition
    {
        public override bool ShouldBreak(DebugSnapshot snapshot)
            => snapshot.IsExportableSubscription;
    }

    /// <summary>Break on bars that produced at least one fill.</summary>
    internal sealed record OnFillBar : BreakCondition
    {
        public override bool ShouldBreak(DebugSnapshot snapshot)
            => snapshot.FillsThisBar > 0;
    }

    /// <summary>Break when sequence number reaches or exceeds the target.</summary>
    internal sealed record AtSequence(long Target) : BreakCondition
    {
        public override bool ShouldBreak(DebugSnapshot snapshot)
            => snapshot.SequenceNumber >= Target;
    }

    /// <summary>Break when timestamp reaches or exceeds the target.</summary>
    internal sealed record AtTimestamp(long TargetMs) : BreakCondition
    {
        public override bool ShouldBreak(DebugSnapshot snapshot)
            => snapshot.TimestampMs >= TargetMs;
    }
}
