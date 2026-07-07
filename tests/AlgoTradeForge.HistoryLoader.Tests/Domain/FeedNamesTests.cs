using AlgoTradeForge.HistoryLoader.Domain;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Domain;

public sealed class FeedNamesTests
{
    [Theory]
    [InlineData(FeedNames.Ticks, true)]
    [InlineData(FeedNames.FundingRate, true)]
    [InlineData(FeedNames.Candles, false)]
    [InlineData(FeedNames.TakerVolume, false)]   // taker-volume keeps interval "15m"
    [InlineData(FeedNames.OpenInterest, false)]
    public void UsesMonthlyCompleteness_ClassifiesIntervalLessFeeds(string feed, bool expected) =>
        Assert.Equal(expected, FeedNames.UsesMonthlyCompleteness(feed));
}
