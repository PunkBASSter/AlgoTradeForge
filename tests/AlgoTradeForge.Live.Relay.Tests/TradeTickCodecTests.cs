using System.Buffers.Binary;
using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class TradeTickCodecTests
{
    [Fact]
    public void RoundTrips()
    {
        var t = new TradeTick(1_700_000_000_001, 5_000_000, 10, 7, AggressorSide.Sell);
        Span<byte> buf = stackalloc byte[TradeTick.PayloadSize];
        Assert.Equal(TradeTick.PayloadSize, t.WriteTo(buf));
        Assert.Equal(t, TradeTick.ReadFrom(buf));
    }

    [Fact]
    public void Format_IncludesAggressor()
        => Assert.Contains("aggressor=Sell",
            new TradeTick(1, 2, 3, 4, AggressorSide.Sell).Format());

    [Fact]
    public void TradeTick_ByteLayout_MatchesSpec()
    {
        var tick = new TradeTick(1, 2, 3, 4, AggressorSide.Sell);
        Span<byte> buf = stackalloc byte[TradeTick.PayloadSize];
        tick.WriteTo(buf);

        Assert.Equal(1L, BinaryPrimitives.ReadInt64LittleEndian(buf[0..]));
        Assert.Equal(2L, BinaryPrimitives.ReadInt64LittleEndian(buf[8..]));
        Assert.Equal(3L, BinaryPrimitives.ReadInt64LittleEndian(buf[16..]));
        Assert.Equal(4L, BinaryPrimitives.ReadInt64LittleEndian(buf[24..]));
        Assert.Equal((byte)AggressorSide.Sell, buf[32]);
    }
}
