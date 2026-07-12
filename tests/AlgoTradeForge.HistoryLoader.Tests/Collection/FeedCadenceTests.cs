using AlgoTradeForge.HistoryLoader.Domain;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class FeedCadenceTests
{
    [Theory]
    [InlineData(FeedNames.MarkPrice, "1h")]
    [InlineData(FeedNames.PremiumIndex, "1h")]
    [InlineData(FeedNames.IndexPrice, "1h")]
    [InlineData(FeedNames.OpenInterest, "5m")]
    [InlineData(FeedNames.LsRatioGlobal, "15m")]
    [InlineData(FeedNames.LsRatioTopAccounts, "15m")]
    [InlineData(FeedNames.TakerVolume, "15m")]
    public void DiskInterval_MatchesCollectorCadence(string feedName, string expected) =>
        Assert.Equal(expected, FeedCadence.DiskInterval(feedName));

    // ls-ratio-top-positions is collected by HourlyCollectorService (1h), NOT RatioCollectorService
    // (15m) — unlike the other two l/s-ratio feeds. Stamping "15m" would request the wrong Binance
    // period and write to a `_15m` partition that diverges from existing `_1h` data.
    [Fact]
    public void DiskInterval_LsRatioTopPositions_Is1h() =>
        Assert.Equal("1h", FeedCadence.DiskInterval(FeedNames.LsRatioTopPositions));

    [Theory]
    [InlineData(FeedNames.Candles)]
    [InlineData(FeedNames.FundingRate)]
    [InlineData(FeedNames.Ticks)]
    [InlineData(FeedNames.Liquidations)]
    [InlineData(FeedNames.BookTicker)]
    public void DiskInterval_IntervalLessFeeds_ReturnEmpty(string feedName) =>
        Assert.Equal("", FeedCadence.DiskInterval(feedName));
}
