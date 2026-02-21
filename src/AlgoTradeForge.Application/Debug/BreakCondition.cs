using AlgoTradeForge.Domain.Engine;

namespace AlgoTradeForge.Application.Debug;

/// <summary>
/// Break condition evaluated at each bar boundary to decide whether the engine should pause.
/// Event-level conditions can also fire at individual event emission points.
/// </summary>
internal abstract record BreakCondition
{
    private BreakCondition() { }

    public abstract bool ShouldBreak(DebugSnapshot snapshot);

    /// <summary>Whether this condition operates at event granularity rather than bar granularity.</summary>
    public virtual bool IsEventLevel => false;

    /// <summary>Evaluate whether to break on an individual event emission.</summary>
    public virtual bool ShouldBreakOnEvent(string eventType, long sequenceNumber) => false;

    public static BreakCondition FromCommand(DebugCommand command) => command switch
    {
        DebugCommand.Continue => Never.Instance,
        DebugCommand.Next => new OnExportableBar(),
        DebugCommand.NextBar => Always.Instance,
        DebugCommand.NextTrade => new OnFillBar(),
        DebugCommand.RunToSequence(var sq) => new AtSequence(sq),
        DebugCommand.RunToTimestamp(var ms) => new AtTimestamp(ms),
        DebugCommand.Pause => Always.Instance,
        DebugCommand.NextSignal => new OnSignal(),
        DebugCommand.NextType(var t) => new OnEventType(t),
        DebugCommand.SetExport => throw new InvalidOperationException(
            "SetExport is a config command, not a stepping command."),
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

    /// <summary>Break when a signal event ("sig") is emitted.</summary>
    internal sealed record OnSignal : BreakCondition
    {
        public override bool IsEventLevel => true;
        public override bool ShouldBreak(DebugSnapshot snapshot) => false;
        public override bool ShouldBreakOnEvent(string eventType, long sequenceNumber)
            => eventType == "sig";
    }

    /// <summary>Break when an event of the specified type is emitted.</summary>
    internal sealed record OnEventType(string Target) : BreakCondition
    {
        public override bool IsEventLevel => true;
        public override bool ShouldBreak(DebugSnapshot snapshot) => false;
        public override bool ShouldBreakOnEvent(string eventType, long sequenceNumber)
            => eventType == Target;
    }

    /// <summary>
    /// Compound condition — both sub-conditions must agree.
    /// Both sub-conditions must operate at the same granularity (both bar-level or both
    /// event-level). Mixing granularities throws <see cref="ArgumentException"/>.
    /// </summary>
    internal sealed record And : BreakCondition
    {
        internal BreakCondition Left { get; }
        internal BreakCondition Right { get; }

        internal And(BreakCondition left, BreakCondition right)
        {
            if (left.IsEventLevel != right.IsEventLevel)
                throw new ArgumentException(
                    "Cannot compose break conditions with different granularities " +
                    $"(left.IsEventLevel={left.IsEventLevel}, right.IsEventLevel={right.IsEventLevel}). " +
                    "Both must be bar-level or both event-level.");
            Left = left;
            Right = right;
        }

        public override bool IsEventLevel => Left.IsEventLevel;
        public override bool ShouldBreak(DebugSnapshot snapshot)
            => Left.ShouldBreak(snapshot) && Right.ShouldBreak(snapshot);
        public override bool ShouldBreakOnEvent(string eventType, long sequenceNumber)
            => Left.ShouldBreakOnEvent(eventType, sequenceNumber)
               && Right.ShouldBreakOnEvent(eventType, sequenceNumber);
    }
}
