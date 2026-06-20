using AlgoTradeForge.Live.Relay;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class TickSegmentHeaderTests
{
    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var header = new TickSegmentHeader(
            PriceScaleExp: 2, QtyScaleExp: 0,
            EpochBaseMs: 0, CreatedAtMs: 1_700_000_000_000, FirstSequence: 99);

        Span<byte> buf = stackalloc byte[RelayFormat.HeaderSize];
        header.WriteTo(buf);

        Assert.True(buf[..4].SequenceEqual("ATFT"u8));
        Assert.Equal(header, TickSegmentHeader.ReadFrom(buf));
    }

    [Fact]
    public void ReadFrom_BadMagic_Throws()
    {
        Span<byte> buf = stackalloc byte[RelayFormat.HeaderSize];
        buf.Fill(0);

        try
        {
            TickSegmentHeader.ReadFrom(buf);
            Assert.Fail("Expected InvalidDataException");
        }
        catch (InvalidDataException) { }
    }
}
