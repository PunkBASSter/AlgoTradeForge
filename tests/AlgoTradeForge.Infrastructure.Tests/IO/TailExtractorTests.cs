using System.Text;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.IO;

public sealed class TailExtractorTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ReturnsNull_ForNullBuffer()
        => Assert.Null(TailExtractor.ExtractLastLine(null!, 0));

    [Fact]
    public void ReturnsNull_ForZeroLength()
        => Assert.Null(TailExtractor.ExtractLastLine(new byte[16], 0));

    [Fact]
    public void ReturnsNull_WhenLengthExceedsBuffer()
        => Assert.Null(TailExtractor.ExtractLastLine(new byte[4], 8));

    [Fact]
    public void ReturnsNull_ForLineTerminatorsOnly()
    {
        var buf = Utf8("\r\n\r\n");
        Assert.Null(TailExtractor.ExtractLastLine(buf, buf.Length));
    }

    [Fact]
    public void ReturnsWholeBuffer_WhenSingleLineNoTerminator()
    {
        var buf = Utf8("1234,7");
        Assert.Equal("1234,7", TailExtractor.ExtractLastLine(buf, buf.Length));
    }

    [Fact]
    public void StripsTrailingLf()
    {
        var buf = Utf8("a\nb\n");
        Assert.Equal("b", TailExtractor.ExtractLastLine(buf, buf.Length));
    }

    [Fact]
    public void StripsTrailingCrLf()
    {
        var buf = Utf8("a\r\nb\r\n");
        Assert.Equal("b", TailExtractor.ExtractLastLine(buf, buf.Length));
    }

    [Fact]
    public void HandlesMultipleTrailingNewlines()
    {
        var buf = Utf8("a\nb\n\n\n");
        Assert.Equal("b", TailExtractor.ExtractLastLine(buf, buf.Length));
    }

    [Fact]
    public void ReturnsLastLine_WhenManyLinesPresent()
    {
        var buf = Utf8("alpha\nbeta\ngamma\ndelta");
        Assert.Equal("delta", TailExtractor.ExtractLastLine(buf, buf.Length));
    }

    [Fact]
    public void HonoursExplicitLengthOverFullBuffer()
    {
        // Underlying buffer is larger than the populated region; only `length` bytes are valid.
        var buf = new byte[64];
        var payload = Utf8("ts,v\n1234,7\n");
        Buffer.BlockCopy(payload, 0, buf, 0, payload.Length);

        Assert.Equal("1234,7", TailExtractor.ExtractLastLine(buf, payload.Length));
    }

    [Fact]
    public void HandlesMixedCrAndLfTerminators()
    {
        var buf = Utf8("first\rsecond\nthird\r\n");
        Assert.Equal("third", TailExtractor.ExtractLastLine(buf, buf.Length));
    }
}
