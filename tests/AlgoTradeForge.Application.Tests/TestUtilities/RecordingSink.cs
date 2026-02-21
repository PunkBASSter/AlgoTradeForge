using AlgoTradeForge.Application.Events;

namespace AlgoTradeForge.Application.Tests.TestUtilities;

/// <summary>
/// Test double that records all serialized event bytes for assertion.
/// </summary>
internal sealed class RecordingSink : ISink
{
    public List<byte[]> Received { get; } = [];

    public void Write(ReadOnlyMemory<byte> utf8Json) => Received.Add(utf8Json.ToArray());
}
