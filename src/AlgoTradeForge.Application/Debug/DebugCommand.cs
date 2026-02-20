namespace AlgoTradeForge.Application.Debug;

/// <summary>
/// All control commands the debug client can send. Sealed hierarchy for exhaustive matching.
/// </summary>
public abstract record DebugCommand
{
    private DebugCommand() { }

    /// <summary>Run to completion without pausing.</summary>
    public sealed record Continue : DebugCommand;

    /// <summary>Advance to the next exported (exportable subscription) bar, then pause.</summary>
    public sealed record Next : DebugCommand;

    /// <summary>Advance to the next bar from any subscription, then pause.</summary>
    public sealed record NextBar : DebugCommand;

    /// <summary>Advance until a bar with fills > 0, then pause.</summary>
    public sealed record NextTrade : DebugCommand;

    /// <summary>Run until a specific sequence number, then pause.</summary>
    public sealed record RunToSequence(long Sq) : DebugCommand;

    /// <summary>Run until a specific timestamp (epoch ms), then pause.</summary>
    public sealed record RunToTimestamp(long Ms) : DebugCommand;

    /// <summary>Pause at the next bar boundary.</summary>
    public sealed record Pause : DebugCommand;
}
