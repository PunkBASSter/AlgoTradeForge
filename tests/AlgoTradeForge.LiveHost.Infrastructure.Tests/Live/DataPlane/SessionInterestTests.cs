using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.DataPlane;

public class SessionInterestTests
{
    private sealed class NoopStrategy : IInt64BarStrategy
    {
        public string Version => "test";
        public IList<DataSubscription> DataSubscriptions { get; } = [];
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }
        public void OnBarStart(Int64Bar bar, DataSubscription subscription) { }
        public void OnBarComplete(Int64Bar bar, DataSubscription subscription) { }
    }

    [Fact]
    public void Build_MismatchedLength_Throws()
    {
        var asset = CryptoAsset.Create("BTCUSDT", "Binance", 2);
        var ch = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropNewest, SingleReader = true });

        // Two resolved subs but only one raw sub: a positional-pairing regression that must fail loudly.
        IReadOnlyList<DataSubscription> resolved =
        [
            new DataSubscription(asset, TimeFrame.Parse("1m")),
            new DataSubscription(asset, default, FeedKey: "tick"),
        ];
        IReadOnlyList<DataFeedSubscription> raw =
        [
            new TimeBarSubscription("BTCUSDT", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1m")),
        ];

        var reg = new LiveSessionRegistration(
            Guid.NewGuid(), new NoopStrategy(), resolved, raw, ch.Writer);

        Assert.Throws<InvalidOperationException>(() => SessionInterest.Build(reg));
    }
}
