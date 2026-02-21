using System.Text.Json;

namespace AlgoTradeForge.Application.Debug;

/// <summary>
/// Parses JSON command messages into <see cref="DebugCommand"/> instances.
/// Shared between WebSocket handler and tests.
/// </summary>
public static class DebugCommandParser
{
    public static (DebugCommand? Command, string? Error) Parse(ReadOnlyMemory<byte> utf8Json)
    {
        using var doc = JsonDocument.Parse(utf8Json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("command", out var commandProp))
            return (null, "Missing 'command' field.");

        var commandStr = commandProp.GetString()?.ToLowerInvariant();
        return commandStr switch
        {
            "continue" => (new DebugCommand.Continue(), null),
            "next" => (new DebugCommand.Next(), null),
            "next_bar" => (new DebugCommand.NextBar(), null),
            "next_trade" => (new DebugCommand.NextTrade(), null),
            "pause" => (new DebugCommand.Pause(), null),
            "run_to_sequence" when root.TryGetProperty("sequenceNumber", out var sq)
                => (new DebugCommand.RunToSequence(sq.GetInt64()), null),
            "run_to_sequence"
                => (null, "Command 'run_to_sequence' requires 'sequenceNumber' field."),
            "run_to_timestamp" when root.TryGetProperty("timestampMs", out var ts)
                => (new DebugCommand.RunToTimestamp(ts.GetInt64()), null),
            "run_to_timestamp"
                => (null, "Command 'run_to_timestamp' requires 'timestampMs' field."),
            _ => (null, $"Unknown command '{commandStr}'.")
        };
    }
}
