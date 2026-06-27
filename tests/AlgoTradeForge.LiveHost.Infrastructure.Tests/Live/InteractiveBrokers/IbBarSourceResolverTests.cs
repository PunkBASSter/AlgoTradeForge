using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbBarSourceResolverTests
{
    private static Asset Aapl() =>
        new EquityAsset { Name = "AAPL", Exchange = "NASDAQ", TickSize = 0.01m };

    private static IbBarSourceResolver Build() => IbBarSourceResolverTestFactory.Create();

    [Fact]
    public void TimeBar_ResolvesToVenueBarSource()
    {
        var resolver = Build();
        var sub = new TimeBarSubscription("AAPL", "ib", DataFeedRole.Primary, TimeFrame.Parse("5s"))
        {
            Asset = Aapl()
        };

        var src = resolver.Resolve("AAPL", sub, new ScaleContext(0.01m), (_, _) => { });

        Assert.IsType<IbVenueBarSource>(src);
    }

    [Fact]
    public void Tick_ResolvesToNull()
    {
        var resolver = Build();
        var sub = new TickSubscription("AAPL", "ib", DataFeedRole.Primary);

        var src = resolver.Resolve("AAPL", sub, new ScaleContext(0.01m), (_, _) => { });

        Assert.Null(src);
    }

    [Fact]
    public void AltBar_ResolvesToTickAggregation()
    {
        var resolver = Build();
        var sub = new AltBarSubscription("AAPL", "ib", DataFeedRole.Primary, "EqV_1m_1000")
        {
            Asset = Aapl()
        };

        var src = resolver.Resolve("AAPL", sub, new ScaleContext(0.01m), (_, _) => { });

        Assert.IsType<TickAggregationBarSource>(src);
    }
}
