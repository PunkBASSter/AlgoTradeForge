using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class RelayFrameFormatterTests
{
    [Fact]
    public void Format_Tick_IncludesFieldsAndAggressor()
    {
        var frame = new RelayFrame(FrameType.Tick, 1_700_000_000_001,
            new TradeTick(1_700_000_000_001, 5_000_000, 10, 7, AggressorSide.Buy), 0);

        var line = RelayFrameFormatter.Format(frame);

        Assert.Contains("TICK", line);
        Assert.Contains("seq=7", line);
        Assert.Contains("price=5000000", line);
        Assert.Contains("Buy", line);
    }

    [Fact]
    public void Format_SessionBoundary_NamesReason()
    {
        var frame = new RelayFrame(FrameType.SessionBoundary, 1_700_000_000_020,
            default, (byte)SessionBoundaryReason.SessionEnd);

        var line = RelayFrameFormatter.Format(frame);

        Assert.Contains("BOUNDARY", line);
        Assert.Contains("SessionEnd", line);
    }
}
