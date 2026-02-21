namespace AlgoTradeForge.Application.Debug;

/// <summary>
/// Wire-format command name constants for the debug WebSocket protocol.
/// </summary>
public static class DebugCommandNames
{
    public const string Continue = "continue";
    public const string Next = "next";
    public const string NextBar = "next_bar";
    public const string NextTrade = "next_trade";
    public const string Pause = "pause";
    public const string RunToSequence = "run_to_sequence";
    public const string RunToTimestamp = "run_to_timestamp";

    public const string NextSignal = "next_signal";
    public const string NextType = "next_type";
    public const string SetExport = "set_export";

    public const string CommandField = "command";
    public const string SequenceNumberField = "sequenceNumber";
    public const string TimestampMsField = "timestampMs";
    public const string EventTypeField = "_t";
    public const string MutationsField = "mutations";
}
