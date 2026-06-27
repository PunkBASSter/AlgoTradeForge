using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbVenueBarSourceTests
{
    private static IbContract AaplSpec() =>
        new("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");

    private static ResolvedIbContract AaplResolved() =>
        new(AaplSpec(), 265598, "AAPL", "");

    [Fact]
    public async Task RealtimeBar_ScalesAndEmits_AndRecords()
    {
        var session = new FakeIbSession();
        var scale = new ScaleContext(0.01m);
        var resolver = Substitute.For<IIbContractResolver>();
        resolver.Resolve(AaplSpec(), Arg.Any<CancellationToken>()).Returns(AaplResolved());
        Int64Bar? emitted = null;
        var src = new IbVenueBarSource(session, resolver, AaplSpec(), scale, (bar, _) => emitted = bar);

        await src.Start();
        session.PushBar(AaplResolved().ConId, new IbRealtimeBar(1_700_000_005L, 1.00, 2.00, 0.50, 1.50, 10m));

        Assert.NotNull(emitted);
        Assert.Equal(1_700_000_005_000L, emitted!.Value.TimestampMs); // seconds → ms
        Assert.Equal(scale.FromMarketPrice(2.00m), emitted.Value.High);
        Assert.Equal(10L, emitted.Value.Volume);
        Assert.Single(src.Recent);
    }
}
