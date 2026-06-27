using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;

public class TimeWatermarkGateTests
{
    private static TradeTick T(long ts) => new(ts, 100, 1, 0, AggressorSide.Unknown);

    [Fact]
    public void FirstTick_SeedsAndAccepts()
    {
        var g = new TimeWatermarkGate(maxGapMs: 1000);
        Assert.Equal(TickAdmission.Accept, g.Admit(T(5000)));
        Assert.True(g.Seeded);
        Assert.Equal(5000, g.LastTimestampMs);
    }

    [Fact]
    public void OlderTimestamp_IsDuplicate()
    {
        var g = new TimeWatermarkGate(1000);
        g.Admit(T(5000));
        Assert.Equal(TickAdmission.Duplicate, g.Admit(T(4999)));
    }

    [Fact]
    public void WithinMaxGap_Accepts_AndAdvances()
    {
        var g = new TimeWatermarkGate(1000);
        g.Admit(T(5000));
        Assert.Equal(TickAdmission.Accept, g.Admit(T(5800)));
        Assert.Equal(5800, g.LastTimestampMs);
    }

    [Fact]
    public void JumpBeyondMaxGap_IsGap_AndDoesNotAdvance()
    {
        var g = new TimeWatermarkGate(1000);
        g.Admit(T(5000));
        Assert.Equal(TickAdmission.Gap, g.Admit(T(7000)));
        Assert.Equal(5000, g.LastTimestampMs); // unchanged until Reseed
    }

    [Fact]
    public void Reseed_MovesWatermarkToTick()
    {
        var g = new TimeWatermarkGate(1000);
        g.Admit(T(5000));
        g.Reseed(T(7000));
        Assert.Equal(7000, g.LastTimestampMs);
    }
}
