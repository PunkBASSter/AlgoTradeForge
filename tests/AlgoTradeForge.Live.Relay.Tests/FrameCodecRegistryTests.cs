using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class FrameCodecRegistryTests
{
    [Fact]
    public void For_Trades_ReturnsCodecThatFormatsTradeToken()
    {
        var codec = FrameCodecRegistry.For("trades");
        var tick = new TradeTick(1_700_000_000_001, 5_000_000, 10, 7, AggressorSide.Buy);
        Span<byte> buf = stackalloc byte[TradeTick.PayloadSize];
        tick.WriteTo(buf);

        var result = codec.FormatFrame(buf);

        Assert.Contains("TRADE", result);
    }

    [Fact]
    public void For_Quotes_ReturnsCodecThatFormatsQuoteToken()
    {
        var codec = FrameCodecRegistry.For("quotes");
        var quote = new QuoteTick(1_700_000_000_001, 4_999_000, 5, 5_001_000, 3, 1);
        Span<byte> buf = stackalloc byte[QuoteTick.PayloadSize];
        quote.WriteTo(buf);

        var result = codec.FormatFrame(buf);

        Assert.Contains("QUOTE", result);
    }

    [Fact]
    public void For_Session_ReturnsCodecThatFormatsSessionToken()
    {
        var codec = FrameCodecRegistry.For("_session");
        var ev = new SessionEvent(1_700_000_000_001, SessionEventKind.SessionStart);
        Span<byte> buf = stackalloc byte[SessionEvent.PayloadSize];
        ev.WriteTo(buf);

        var result = codec.FormatFrame(buf);

        Assert.Contains("SESSION", result);
    }

    [Fact]
    public void For_Unknown_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => FrameCodecRegistry.For("nope"));
    }

    [Fact]
    public void Codec_StreamName_MatchesExpected()
    {
        Assert.Equal("trades",   FrameCodecRegistry.For("trades").StreamName);
        Assert.Equal("quotes",   FrameCodecRegistry.For("quotes").StreamName);
        Assert.Equal("_session", FrameCodecRegistry.For("_session").StreamName);
    }

    [Fact]
    public void Codec_PayloadSize_MatchesFramePayloadType()
    {
        Assert.Equal(TradeTick.PayloadSize,   FrameCodecRegistry.For("trades").PayloadSize);
        Assert.Equal(QuoteTick.PayloadSize,   FrameCodecRegistry.For("quotes").PayloadSize);
        Assert.Equal(SessionEvent.PayloadSize, FrameCodecRegistry.For("_session").PayloadSize);
    }
}
