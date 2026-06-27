using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.DataPlane;

public class TickAggregationBarSourceGateTests
{
    // A spy gate that records how many ticks it admitted, proving the injected instance is used.
    private sealed class CountingGate : ICatchupGate
    {
        public int Admitted { get; private set; }
        public bool Seeded { get; private set; }
        public long LastTimestampMs { get; private set; }
        public TickAdmission Admit(in TradeTick tick)
        { Admitted++; Seeded = true; LastTimestampMs = tick.TimestampMs; return TickAdmission.Accept; }
        public void Reseed(in TradeTick tick) { LastTimestampMs = tick.TimestampMs; }
    }

    [Fact]
    public void Feed_UsesInjectedGate()
    {
        var gate = new CountingGate();
        // EqV threshold large enough that no bar emits; we only assert the gate saw the tick.
        var src = new TickAggregationBarSource(
            "EqV", frozenThreshold: long.MaxValue, scale: new ScaleContext(tickSize: 0.01m),
            onBar: (_, _) => { }, gate: gate);

        src.Feed(new TradeTick(1000, 100, 1, 0, AggressorSide.Unknown));

        Assert.Equal(1, gate.Admitted);
    }
}
