using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Collection;

public class CollectionCoverageTests
{
    private static readonly DataFeedSubscription[] Collected =
    [
        new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary),
        new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1m")),
        new SideFeedSubscription("BTCUSDT", "binance", DataFeedRole.Side, "funding-rate"),
    ];

    // kind, asset, detail (interval/feedId; null for tick), expectedSatisfied
    [Theory]
    [InlineData("tick", "BTCUSDT", null, true)]            // tick collected
    [InlineData("tick", "ETHUSDT", null, false)]           // wrong asset
    [InlineData("timebar", "BTCUSDT", "1m", true)]         // interval matches
    [InlineData("timebar", "BTCUSDT", "1h", false)]        // interval differs (exact, not divisor)
    [InlineData("side", "BTCUSDT", "funding-rate", true)]  // side feed collected
    [InlineData("side", "BTCUSDT", "open-interest", false)] // side feed wrong FeedId
    [InlineData("altbar", "BTCUSDT", "EqV_ticks_1000", true)]   // root = collected tick
    [InlineData("altbar", "BTCUSDT", "EqV_1m_1000", true)]      // root = collected 1m candle
    [InlineData("altbar", "BTCUSDT", "EqV_5m_1000", false)]     // root = 5m candle (not collected)
    [InlineData("altbar", "BTCUSDT", "not-a-valid-feedid", false)] // malformed -> unmet, not thrown
    public void Coverage_against_collected_set(string kind, string asset, string? detail, bool expectedSatisfied)
    {
        DataFeedSubscription required = kind switch
        {
            "tick" => new TickSubscription(asset, "binance", DataFeedRole.Primary),
            "timebar" => new TimeBarSubscription(asset, "binance", DataFeedRole.Primary, TimeFrame.Parse(detail!)),
            "side" => new SideFeedSubscription(asset, "binance", DataFeedRole.Side, detail!),
            "altbar" => new AltBarSubscription(asset, "binance", DataFeedRole.Primary, detail!),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown subscription kind"),
        };

        var unmet = CollectionCoverage.FindUnmet(Collected, [required]);

        if (expectedSatisfied)
        {
            Assert.Null(unmet);
        }
        else
        {
            Assert.NotNull(unmet);
            Assert.Contains(asset, unmet);
        }
    }
}
