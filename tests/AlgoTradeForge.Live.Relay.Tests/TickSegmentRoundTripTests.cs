using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class TickSegmentRoundTripTests
{
    [Fact]
    public void TicksAndMarkers_SurviveWriteReadCycle()
    {
        var header = new TickSegmentHeader(2, 0, 0, 1_700_000_000_000, 1);
        var ticks = new[]
        {
            new TradeTick(1_700_000_000_001, 5_000_000, 10, 1, AggressorSide.Buy),
            new TradeTick(1_700_000_000_002, 5_000_050, 20, 2, AggressorSide.Unknown),
            new TradeTick(1_700_000_000_005, 4_999_900, 7,  3, AggressorSide.Sell),
        };

        using var ms = new MemoryStream();
        using (var writer = new TickSegmentWriter(ms, header, leaveOpen: true))
        {
            writer.WriteSessionBoundary(1_700_000_000_000, SessionBoundaryReason.SessionStart);
            foreach (var t in ticks) writer.WriteTick(t);
            writer.WriteHeartbeat(1_700_000_000_010);
            writer.WriteSessionBoundary(1_700_000_000_020, SessionBoundaryReason.SessionEnd);
        }

        ms.Position = 0;
        using var reader = new TickSegmentReader(ms);

        Assert.Equal(header, reader.Header);

        Assert.True(reader.TryReadFrame(out var f0));
        Assert.Equal(FrameType.SessionBoundary, f0.Type);
        Assert.Equal((byte)SessionBoundaryReason.SessionStart, f0.ReasonCode);

        foreach (var expected in ticks)
        {
            Assert.True(reader.TryReadFrame(out var f));
            Assert.Equal(FrameType.Tick, f.Type);
            Assert.Equal(expected, f.Trade);
        }

        Assert.True(reader.TryReadFrame(out var hb));
        Assert.Equal(FrameType.Heartbeat, hb.Type);
        Assert.Equal(1_700_000_000_010, hb.TimestampMs);

        Assert.True(reader.TryReadFrame(out var end));
        Assert.Equal(FrameType.SessionBoundary, end.Type);
        Assert.Equal((byte)SessionBoundaryReason.SessionEnd, end.ReasonCode);

        Assert.False(reader.TryReadFrame(out _));
    }

    [Fact]
    public void TornFrame_Throws()
    {
        var header = new TickSegmentHeader(2, 0, 0, 1, 1);
        using var ms = new MemoryStream();
        using (var writer = new TickSegmentWriter(ms, header, leaveOpen: true))
            writer.WriteTick(new TradeTick(1, 2, 3, 4, AggressorSide.Unknown));

        ms.SetLength(ms.Length - 3); // truncate the last frame
        ms.Position = 0;

        using var reader = new TickSegmentReader(ms);
        Assert.Throws<EndOfStreamException>((Action)(() => reader.TryReadFrame(out _)));
    }
}
