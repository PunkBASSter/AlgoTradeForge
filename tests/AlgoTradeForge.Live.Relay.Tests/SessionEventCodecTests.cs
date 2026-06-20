using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class SessionEventCodecTests
{
    [Fact]
    public void RoundTrips_AllKinds()
    {
        Span<byte> buf = stackalloc byte[SessionEvent.PayloadSize];
        foreach (var kind in Enum.GetValues<SessionEventKind>())
        {
            var e = new SessionEvent(1_700_000_000_001, kind);
            Assert.Equal(SessionEvent.PayloadSize, e.WriteTo(buf));
            Assert.Equal(e, SessionEvent.ReadFrom(buf));
        }
    }

    [Fact]
    public void Format_ContainsKind()
    {
        var f = new SessionEvent(1, SessionEventKind.Heartbeat).Format();
        Assert.Contains("kind=Heartbeat", f);
    }
}
