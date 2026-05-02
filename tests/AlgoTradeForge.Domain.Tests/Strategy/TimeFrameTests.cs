using AlgoTradeForge.Domain.Strategy;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy;

/// <summary>
/// Unit tests for <see cref="TimeFrame"/> (Phase 4 / P4-1).
/// </summary>
public class TimeFrameTests
{
    [Theory]
    [InlineData("1m", 60)]
    [InlineData("15m", 900)]
    [InlineData("1h", 3600)]
    [InlineData("4h", 14400)]
    [InlineData("1d", 86400)]
    public void Parse_RoundTripsThroughCode(string code, int expectedSeconds)
    {
        // Parse → Code must be the identity for canonical shorthand. The grammar is shared
        // with feed-id composition (TRD §3.3); divergence here would corrupt feed names.
        var tf = TimeFrame.Parse(code);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), tf.Duration);
        Assert.Equal(code, tf.Code);
        Assert.Equal(code, tf.ToString());
    }

    [Fact]
    public void Parse_InvalidInput_Throws()
    {
        // Symmetric with TimeSpan.Parse — surface the bad input in the message so config /
        // request-payload mistakes have a debuggable error.
        var ex = Assert.Throws<FormatException>(() => TimeFrame.Parse("not-a-frame"));
        Assert.Contains("not-a-frame", ex.Message);
    }

    [Fact]
    public void Parse_Null_ErrorMessageMentionsNullExplicitly()
    {
        // "Invalid TimeFrame: ''" can't distinguish empty from null — be explicit so
        // payload bugs are debuggable.
        var ex = Assert.Throws<FormatException>(() => TimeFrame.Parse(null!));
        Assert.Contains("<null>", ex.Message);
    }

    [Theory]
    [InlineData("1m")]
    [InlineData("15m")]
    [InlineData("1h")]
    public void TryParse_ValidInputs_ReturnTrueAndPopulate(string code)
    {
        Assert.True(TimeFrame.TryParse(code, out var tf));
        Assert.Equal(code, tf.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("foo")]
    [InlineData("1y")]      // 'y' is not in the suffix set
    [InlineData("0m")]      // zero / negative rejected by formatter
    public void TryParse_InvalidInputs_ReturnFalseAndDefault(string? code)
    {
        Assert.False(TimeFrame.TryParse(code, out var tf));
        Assert.Equal(default, tf);
    }

    [Fact]
    public void Duration_ExposesUnderlyingTimeSpan_ForArithmetic()
    {
        // Read-side callsites (CsvDataSource, HistoryRepository) compare TimeFrames against
        // raw TimeSpans via .Duration. This is the documented escape hatch.
        var tf = TimeFrame.Parse("1h");
        Assert.True(tf.Duration > TimeSpan.FromMinutes(30));
        Assert.Equal(3_600_000, (long)tf.Duration.TotalMilliseconds);
    }

    [Fact]
    public void RecordStructEquality_SameDuration_AreEqual()
    {
        // record struct gives us value equality for free — strategies / catalog code may
        // rely on this when grouping by timeframe.
        var a = new TimeFrame(TimeSpan.FromMinutes(15));
        var b = TimeFrame.Parse("15m");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData(90)]      // 1.5 min — Format would silently truncate to "1m" (60s)
    [InlineData(0)]       // zero
    [InlineData(-60)]     // negative
    [InlineData(7200 + 1)] // 2h + 1s — Format would truncate to "2h"
    public void Constructor_NonCanonicalDuration_Throws(int seconds)
    {
        // Phase 4 type-safety guarantee: a TimeFrame must round-trip through Code → Parse.
        // Without this guard, 90s would silently format as "1m" and parse back as 60s,
        // losing 30s — defeating the whole point of moving off raw TimeSpan.
        var ex = Assert.Throws<ArgumentException>(
            () => new TimeFrame(TimeSpan.FromSeconds(seconds)));
        Assert.Contains("canonical", ex.Message);
    }

    [Theory]
    [InlineData("1m", 60)]            // shorthand
    [InlineData("15m", 900)]
    [InlineData("1h", 3600)]
    [InlineData("45s", 45)]
    [InlineData("00:01:00", 60)]      // hh:mm:ss (live / optimization wire form)
    [InlineData("00:15:00", 900)]
    [InlineData("01:00:00", 3600)]
    [InlineData("00:00:45", 45)]      // sub-minute via wire form is canonical (round-trips via "45s")
    [InlineData("1.00:00:00", 86400)] // d.hh:mm:ss
    public void TryParseLiberal_AcceptsBothFormats(string input, int expectedSeconds)
    {
        // Single boundary parser for backtest / live / optimization request DTOs (TRD §9.1)
        // — the three callsites used to drift on which forms they accepted; now they share
        // one accept set.
        Assert.True(TimeFrame.TryParseLiberal(input, out var tf));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), tf.Duration);
    }

    [Theory]
    [InlineData("00:01:30")]   // 90s — would silently truncate to "1m"; reject loudly instead
    [InlineData("00:00:00")]   // zero
    [InlineData("not-a-frame")]
    [InlineData(null)]
    [InlineData("")]
    public void TryParseLiberal_RejectsNonCanonicalAndInvalid(string? input)
    {
        Assert.False(TimeFrame.TryParseLiberal(input, out var rejected));
        Assert.Equal(default, rejected);
    }
}
