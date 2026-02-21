using System.Text.Json;
using AlgoTradeForge.Application.Debug;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

/// <summary>
/// Tests that WebSocket command JSON messages produce the correct DebugCommand.
/// Validates the contract between the FE client and the WebSocket handler.
/// Calls <see cref="DebugCommandParser.Parse"/> directly (accessible via InternalsVisibleTo).
/// </summary>
public sealed class DebugCommandParsingTests
{
    [Theory]
    [InlineData("continue", typeof(DebugCommand.Continue))]
    [InlineData("next", typeof(DebugCommand.Next))]
    [InlineData("next_bar", typeof(DebugCommand.NextBar))]
    [InlineData("next_trade", typeof(DebugCommand.NextTrade))]
    [InlineData("pause", typeof(DebugCommand.Pause))]
    public void ParseCommand_SimpleCommands_ProducesCorrectType(string commandStr, Type expectedType)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { command = commandStr });
        var (command, error) = DebugCommandParser.Parse(json);

        Assert.NotNull(command);
        Assert.Null(error);
        Assert.IsType(expectedType, command);
    }

    [Fact]
    public void ParseCommand_RunToSequence_ProducesCorrectCommandWithValue()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { command = "run_to_sequence", sequenceNumber = 42L });
        var (command, error) = DebugCommandParser.Parse(json);

        Assert.Null(error);
        var rts = Assert.IsType<DebugCommand.RunToSequence>(command);
        Assert.Equal(42L, rts.Sq);
    }

    [Fact]
    public void ParseCommand_RunToTimestamp_ProducesCorrectCommandWithValue()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { command = "run_to_timestamp", timestampMs = 1704067200000L });
        var (command, error) = DebugCommandParser.Parse(json);

        Assert.Null(error);
        var rtt = Assert.IsType<DebugCommand.RunToTimestamp>(command);
        Assert.Equal(1704067200000L, rtt.Ms);
    }

    [Fact]
    public void ParseCommand_RunToSequence_MissingValue_ReturnsError()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { command = "run_to_sequence" });
        var (command, error) = DebugCommandParser.Parse(json);

        Assert.Null(command);
        Assert.Contains("sequenceNumber", error);
    }

    [Fact]
    public void ParseCommand_RunToTimestamp_MissingValue_ReturnsError()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { command = "run_to_timestamp" });
        var (command, error) = DebugCommandParser.Parse(json);

        Assert.Null(command);
        Assert.Contains("timestampMs", error);
    }

    [Fact]
    public void ParseCommand_UnknownCommand_ReturnsError()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { command = "unknown_cmd" });
        var (command, error) = DebugCommandParser.Parse(json);

        Assert.Null(command);
        Assert.Contains("Unknown command", error);
    }

    [Fact]
    public void ParseCommand_MissingCommandField_ReturnsError()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { foo = "bar" });
        var (command, error) = DebugCommandParser.Parse(json);

        Assert.Null(command);
        Assert.Contains("command", error);
    }

    [Theory]
    [InlineData("CONTINUE")]
    [InlineData("Continue")]
    [InlineData("NEXT_BAR")]
    public void ParseCommand_CaseInsensitive(string commandStr)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { command = commandStr });
        var (command, error) = DebugCommandParser.Parse(json);

        Assert.NotNull(command);
        Assert.Null(error);
    }
}
