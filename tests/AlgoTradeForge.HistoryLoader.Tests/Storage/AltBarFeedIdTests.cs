using System.Globalization;
using AlgoTradeForge.HistoryLoader.Domain;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Storage;

/// <summary>
/// P1a-3, P1a-4 — positional alt-bar feed-id grammar (TRD §3.3).
/// </summary>
public sealed class AltBarFeedIdTests
{
    // ---- Round-trip happy path -----------------------------------------------

    [Theory]
    [InlineData("EqV_1m_1000",       "EqV", "1m",   1000L,  '\0')]
    [InlineData("EqT_5m_500",        "EqT", "5m",   500L,   '\0')]
    [InlineData("EqD_1h_1000000",    "EqD", "1h",   1000000L, '\0')]
    [InlineData("EqI_ticks_500000",  "EqI", "ticks", 500000L, '\0')]
    [InlineData("EqID_ticks_5000",   "EqID", "ticks", 5000L,  '\0')]
    [InlineData("EqIT_1m_1000",      "EqIT", "1m",   1000L,  '\0')]
    [InlineData("Range_1m_50",       "Range", "1m", 50L,    '\0')]
    [InlineData("Renko_1d_10",       "Renko", "1d", 10L,    '\0')]
    public void Parse_RoundTripsIntegerThresholds(
        string input, string typeCode, string sourceCode, long mantissa, char suffix)
    {
        var parsed = AltBarFeedId.Parse(input);

        Assert.Equal(typeCode, parsed.TypeCode);
        Assert.Equal(sourceCode, parsed.SourceCode);
        Assert.Equal(mantissa, parsed.Threshold.Mantissa);
        Assert.Equal(suffix, parsed.Threshold.Suffix);
        Assert.False(parsed.IsSidecar);
        Assert.Equal(input, parsed.FeedId);
        Assert.Equal(input, parsed.DirectoryName);
    }

    // ---- SI-suffix thresholds (k / M / G / m / u) ----------------------------

    [Theory]
    [InlineData("EqV_1m_1k",        1L,  'k',  "1000")]
    [InlineData("EqV_1m_500m",      500L, 'm', "0.5")]      // X-5 ambiguity fixture
    [InlineData("EqV_5m_500m",      500L, 'm', "0.5")]      // X-5 ambiguity fixture
    [InlineData("EqV_1m_1u",        1L,  'u',  "0.000001")] // minimum effective threshold
    [InlineData("EqD_1h_2M",        2L,  'M',  "2000000")]
    [InlineData("EqD_1h_1G",        1L,  'G',  "1000000000")]
    [InlineData("EqID_ticks_5M",    5L,  'M',  "5000000")]   // dollar-imbalance: $5M threshold
    [InlineData("EqIT_1m_1k",       1L,  'k',  "1000")]      // tick-count-imbalance: 1k count threshold
    public void Parse_HandlesSiSuffixes(string input, long mantissa, char suffix, string absoluteValueRaw)
    {
        var absoluteValue = decimal.Parse(absoluteValueRaw, CultureInfo.InvariantCulture);
        var parsed = AltBarFeedId.Parse(input);

        Assert.Equal(mantissa, parsed.Threshold.Mantissa);
        Assert.Equal(suffix, parsed.Threshold.Suffix);
        Assert.Equal(absoluteValue, parsed.Threshold.AbsoluteValue);
        Assert.Equal(input, parsed.FeedId);
    }

    // ---- Sidecar (.flow) form ------------------------------------------------

    [Fact]
    public void Parse_DetectsFlowSidecar()
    {
        var parsed = AltBarFeedId.Parse("EqI_ticks_500000.flow");

        Assert.True(parsed.IsSidecar);
        Assert.Equal("EqI", parsed.TypeCode);
        Assert.Equal("ticks", parsed.SourceCode);
        Assert.Equal(500000L, parsed.Threshold.Mantissa);
        Assert.Equal("EqI_ticks_500000", parsed.FeedId);
        Assert.Equal("EqI_ticks_500000.flow", parsed.DirectoryName);
    }

    [Fact]
    public void Parse_DetectsFlowSidecar_EqID()
    {
        var parsed = AltBarFeedId.Parse("EqID_ticks_5M.flow");

        Assert.True(parsed.IsSidecar);
        Assert.Equal("EqID", parsed.TypeCode);
        Assert.Equal("EqID_ticks_5M", parsed.FeedId);
        Assert.Equal("EqID_ticks_5M.flow", parsed.DirectoryName);
    }

    [Fact]
    public void Parse_DetectsFlowSidecar_EqIT()
    {
        var parsed = AltBarFeedId.Parse("EqIT_1m_1k.flow");

        Assert.True(parsed.IsSidecar);
        Assert.Equal("EqIT", parsed.TypeCode);
        Assert.Equal("EqIT_1m_1k", parsed.FeedId);
    }

    [Fact]
    public void DirectoryName_RoundTripsAcrossFlowToggle()
    {
        var bar = AltBarFeedId.Parse("EqV_1m_1000");
        var sidecar = bar with { IsSidecar = true };

        Assert.Equal("EqV_1m_1000",      bar.DirectoryName);
        Assert.Equal("EqV_1m_1000.flow", sidecar.DirectoryName);
    }

    // ---- X-5 ambiguity fixtures the parser MUST resolve positionally --------

    [Fact]
    public void Parse_EqV_1m_500m_DisambiguatesPositionally()
    {
        // Component 2 ("1m") matches the source-code set; component 3 ("500m")
        // is parsed as 500-with-milli-suffix. The parser does NOT scan content
        // to disambiguate — splits on '_', validates each slot.
        var parsed = AltBarFeedId.Parse("EqV_1m_500m");

        Assert.Equal("1m", parsed.SourceCode);
        Assert.Equal(0.5m, parsed.Threshold.AbsoluteValue);
    }

    [Fact]
    public void Parse_EqV_5m_500m_SameDisambiguation()
    {
        var parsed = AltBarFeedId.Parse("EqV_5m_500m");

        Assert.Equal("5m", parsed.SourceCode);
        Assert.Equal(0.5m, parsed.Threshold.AbsoluteValue);
    }

    [Fact]
    public void Parse_EqV_1d_1d_RejectsInvalidThresholdSuffix()
    {
        // 'd' is NOT a valid SI suffix. The threshold component is invalid;
        // parser rejects rather than silently mis-routing.
        Assert.Throws<FormatException>(() => AltBarFeedId.Parse("EqV_1d_1d"));
    }

    // ---- Negative cases ------------------------------------------------------

    [Theory]
    [InlineData("",                "empty")]
    [InlineData("   ",             "whitespace")]
    [InlineData("EqV",             "1 component")]
    [InlineData("EqV_1m",          "2 components")]
    [InlineData("EqV_1m_1000_extra", "4 components")]
    [InlineData("Foo_1m_1000",     "bad type code")]
    [InlineData("EqV_2y_1000",     "bad source code")]
    [InlineData("EqV_1m_0",        "zero threshold")]
    [InlineData("EqV_1m_-1",       "negative threshold")]
    [InlineData("EqV_1m_abc",      "non-numeric mantissa")]
    [InlineData("EqV_1m_1.5",      "fractional mantissa rejected — use SI suffix instead")]
    public void Parse_ThrowsOnInvalidInput(string input, string description)
    {
        Assert.Throws<FormatException>(() => AltBarFeedId.Parse(input));
        _ = description; // documentation only
    }

    [Fact]
    public void TryParse_NonThrowingFormReturnsFalseWithError()
    {
        var ok = AltBarFeedId.TryParse("EqV_1m_abc", out var result, out var error);

        Assert.False(ok);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("threshold", error, StringComparison.OrdinalIgnoreCase);
    }

    // ---- ThresholdValue micro-tests -----------------------------------------

    [Theory]
    [InlineData(1L, '\0', "1")]
    [InlineData(1L, 'k',  "1000")]
    [InlineData(1L, 'M',  "1000000")]
    [InlineData(1L, 'G',  "1000000000")]
    [InlineData(1L, 'm',  "0.001")]
    [InlineData(1L, 'u',  "0.000001")]
    public void ThresholdValue_AbsoluteValueMatchesSuffixMultiplier(
        long mantissa, char suffix, string expectedRaw)
    {
        var expected = decimal.Parse(expectedRaw, CultureInfo.InvariantCulture);
        var t = new ThresholdValue(mantissa, suffix);
        Assert.Equal(expected, t.AbsoluteValue);
    }

    [Fact]
    public void ThresholdValue_NoSuffix_CanonicalStringIsBareInteger()
    {
        Assert.Equal("1000", new ThresholdValue(1000L, '\0').ToCanonicalString());
    }

    [Fact]
    public void ThresholdValue_WithSuffix_CanonicalStringAppendsSuffix()
    {
        Assert.Equal("500m", new ThresholdValue(500L, 'm').ToCanonicalString());
    }
}
