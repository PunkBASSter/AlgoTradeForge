using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class QuoteTickCodecTests
{
    [Fact]
    public void RoundTrips()
    {
        var q = new QuoteTick(1_700_000_000_001, 4_999_000, 25, 5_001_000, 30, 42);
        Span<byte> buf = stackalloc byte[QuoteTick.PayloadSize];
        Assert.Equal(QuoteTick.PayloadSize, q.WriteTo(buf));
        Assert.Equal(q, QuoteTick.ReadFrom(buf));
    }

    [Fact]
    public void Format_ContainsBidAndAsk()
    {
        var f = new QuoteTick(1, 100, 10, 200, 20, 1).Format();
        Assert.Contains("bid=", f);
        Assert.Contains("ask=", f);
    }
}
