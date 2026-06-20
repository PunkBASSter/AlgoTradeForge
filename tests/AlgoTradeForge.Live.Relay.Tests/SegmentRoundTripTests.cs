using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class SegmentRoundTripTests
{
    private static SegmentHeader MakeHeader<T>(long firstSeq = 1) where T : IFramePayload<T>
        => new(PriceScaleExp: 2, QtyScaleExp: 0,
               EpochBaseMs: 0, CreatedAtMs: 1_700_000_000_000,
               FirstSequence: firstSeq,
               PayloadSize: (ushort)T.PayloadSize);

    [Fact]
    public void TradeTick_RoundTrips()
    {
        var header = MakeHeader<TradeTick>();
        var trades = new[]
        {
            new TradeTick(1_700_000_000_001, 5_000_000, 10, 1, AggressorSide.Buy),
            new TradeTick(1_700_000_000_002, 5_000_050, 20, 2, AggressorSide.Unknown),
            new TradeTick(1_700_000_000_005, 4_999_900,  7, 3, AggressorSide.Sell),
        };

        using var ms = new MemoryStream();
        using (var writer = new SegmentWriter<TradeTick>(ms, header, leaveOpen: true))
            foreach (var t in trades) writer.Write(t);

        ms.Position = 0;
        using var reader = new SegmentReader<TradeTick>(ms);

        Assert.Equal(header, reader.Header);
        foreach (var expected in trades)
        {
            Assert.True(reader.TryRead(out var actual));
            Assert.Equal(expected, actual);
        }
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void QuoteTick_RoundTrips()
    {
        var header = MakeHeader<QuoteTick>();
        var quotes = new[]
        {
            new QuoteTick(1_700_000_000_001, 4_999_000, 5, 5_001_000, 3, 1),
            new QuoteTick(1_700_000_000_002, 4_998_500, 8, 5_001_500, 2, 2),
        };

        using var ms = new MemoryStream();
        using (var writer = new SegmentWriter<QuoteTick>(ms, header, leaveOpen: true))
            foreach (var q in quotes) writer.Write(q);

        ms.Position = 0;
        using var reader = new SegmentReader<QuoteTick>(ms);

        Assert.Equal(header, reader.Header);
        foreach (var expected in quotes)
        {
            Assert.True(reader.TryRead(out var actual));
            Assert.Equal(expected, actual);
        }
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void SessionEvent_RoundTrips()
    {
        var header = MakeHeader<SessionEvent>();
        var events = new[]
        {
            new SessionEvent(1_700_000_000_001, SessionEventKind.SessionStart),
            new SessionEvent(1_700_000_000_002, SessionEventKind.Heartbeat),
            new SessionEvent(1_700_000_000_003, SessionEventKind.ConnectorRestart),
            new SessionEvent(1_700_000_000_004, SessionEventKind.SessionEnd),
        };

        using var ms = new MemoryStream();
        using (var writer = new SegmentWriter<SessionEvent>(ms, header, leaveOpen: true))
            foreach (var e in events) writer.Write(e);

        ms.Position = 0;
        using var reader = new SegmentReader<SessionEvent>(ms);

        Assert.Equal(header, reader.Header);
        foreach (var expected in events)
        {
            Assert.True(reader.TryRead(out var actual));
            Assert.Equal(expected, actual);
        }
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void TornFrame_Throws()
    {
        var header = MakeHeader<TradeTick>();
        using var ms = new MemoryStream();
        using (var writer = new SegmentWriter<TradeTick>(ms, header, leaveOpen: true))
            writer.Write(new TradeTick(1, 2, 3, 4, AggressorSide.Unknown));

        ms.SetLength(ms.Length - 3);
        ms.Position = 0;

        using var reader = new SegmentReader<TradeTick>(ms);
        Assert.Throws<EndOfStreamException>(() => reader.TryRead(out _));
    }

    [Fact]
    public void PayloadSizeMismatch_Throws()
    {
        var header = MakeHeader<TradeTick>();
        using var ms = new MemoryStream();
        using (var writer = new SegmentWriter<TradeTick>(ms, header, leaveOpen: true))
            writer.Write(new TradeTick(1, 2, 3, 4, AggressorSide.Buy));

        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => new SegmentReader<QuoteTick>(ms));
    }
}
