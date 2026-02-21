using AlgoTradeForge.Domain.Events;

namespace AlgoTradeForge.Domain.Tests.TestUtilities;

/// <summary>
/// Test double that captures all emitted events for assertion.
/// </summary>
internal sealed class CapturingEventBus : IEventBus
{
    public List<object> Events { get; } = [];

    public void Emit<T>(T evt) where T : IBacktestEvent => Events.Add(evt);
}
