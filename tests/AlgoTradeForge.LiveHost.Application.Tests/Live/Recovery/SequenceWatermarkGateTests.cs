using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;

public class SequenceWatermarkGateTests
{
    private static TradeTick Tick(long seq, long ts = 0) =>
        new(TimestampMs: ts, Price: 100, Quantity: 1, Sequence: seq, Aggressor: AggressorSide.Buy);

    [Fact]
    public void First_tick_is_accepted_and_seeds_watermark()
    {
        var gate = new SequenceWatermarkGate();
        Assert.Equal(TickAdmission.Accept, gate.Admit(Tick(5)));
        Assert.True(gate.Seeded);
        Assert.Equal(5, gate.LastSequence);
    }

    [Fact]
    public void Contiguous_tick_accepted_duplicate_dropped_gap_flagged()
    {
        var gate = new SequenceWatermarkGate();
        gate.Admit(Tick(5));
        Assert.Equal(TickAdmission.Accept, gate.Admit(Tick(6)));     // contiguous
        Assert.Equal(TickAdmission.Duplicate, gate.Admit(Tick(6))); // replay/live overlap
        Assert.Equal(TickAdmission.Duplicate, gate.Admit(Tick(4))); // older
        Assert.Equal(TickAdmission.Gap, gate.Admit(Tick(9)));       // jump
        Assert.Equal(6, gate.LastSequence);                              // gap did NOT advance
    }

    [Fact]
    public void Reseed_accepts_the_new_contiguous_run()
    {
        var gate = new SequenceWatermarkGate();
        gate.Admit(Tick(5));
        Assert.Equal(TickAdmission.Gap, gate.Admit(Tick(20)));
        gate.Reseed(Tick(20));                                           // discontinuity declared at 20
        Assert.Equal(TickAdmission.Accept, gate.Admit(Tick(21)));
    }
}
