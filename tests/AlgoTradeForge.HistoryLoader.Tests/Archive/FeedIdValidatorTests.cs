using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class FeedIdValidatorTests
{
    [Theory]
    [InlineData("binance")]
    [InlineData("BTCUSDT")]
    [InlineData("BTCUSDT_perp")]
    [InlineData("1000SHIBUSDT")]
    [InlineData("brk.b")]
    public void PathComponent_Legitimate_Passes(string value) =>
        Assert.True(FeedIdValidator.TryValidatePathComponent(value, out _));

    [Theory]
    [InlineData("C:evil")]        // drive-relative — the gap this task closes
    [InlineData(@"C:\evil")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a..b")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    public void PathComponent_Hostile_Fails(string value) =>
        Assert.False(FeedIdValidator.TryValidatePathComponent(value, out _));

    [Fact]
    public void SourceFeedId_DriveRelative_Fails() =>
        Assert.False(FeedIdValidator.TryValidateSourceFeedId("C:1m", out _));
}
