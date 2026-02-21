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

        if (!root.TryGetProperty(DebugCommandNames.CommandField, out var commandProp))
            return (null, $"Missing '{DebugCommandNames.CommandField}' field.");

        var commandStr = commandProp.GetString()?.ToLowerInvariant();
        return commandStr switch
        {
            DebugCommandNames.Continue => (new DebugCommand.Continue(), null),
            DebugCommandNames.Next => (new DebugCommand.Next(), null),
            DebugCommandNames.NextBar => (new DebugCommand.NextBar(), null),
            DebugCommandNames.NextTrade => (new DebugCommand.NextTrade(), null),
            DebugCommandNames.Pause => (new DebugCommand.Pause(), null),
            DebugCommandNames.RunToSequence when root.TryGetProperty(DebugCommandNames.SequenceNumberField, out var sq)
                => (new DebugCommand.RunToSequence(sq.GetInt64()), null),
            DebugCommandNames.RunToSequence
                => (null, $"Command '{DebugCommandNames.RunToSequence}' requires '{DebugCommandNames.SequenceNumberField}' field."),
            DebugCommandNames.RunToTimestamp when root.TryGetProperty(DebugCommandNames.TimestampMsField, out var ts)
                => (new DebugCommand.RunToTimestamp(ts.GetInt64()), null),
            DebugCommandNames.RunToTimestamp
                => (null, $"Command '{DebugCommandNames.RunToTimestamp}' requires '{DebugCommandNames.TimestampMsField}' field."),
            DebugCommandNames.NextSignal => (new DebugCommand.NextSignal(), null),
            DebugCommandNames.NextType when root.TryGetProperty(DebugCommandNames.EventTypeField, out var t)
                => (new DebugCommand.NextType(t.GetString()!), null),
            DebugCommandNames.NextType
                => (null, $"Command '{DebugCommandNames.NextType}' requires '{DebugCommandNames.EventTypeField}' field."),
            DebugCommandNames.SetExport when root.TryGetProperty(DebugCommandNames.MutationsField, out var m)
                => (new DebugCommand.SetExport(m.GetBoolean()), null),
            DebugCommandNames.SetExport
                => (null, $"Command '{DebugCommandNames.SetExport}' requires '{DebugCommandNames.MutationsField}' field."),
            _ => (null, $"Unknown command '{commandStr}'.")
        };
    }
}
