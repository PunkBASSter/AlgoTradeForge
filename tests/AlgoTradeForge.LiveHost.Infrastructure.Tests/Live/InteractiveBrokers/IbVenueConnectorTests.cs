using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbVenueConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Identity_IsIbSingleSession()
    {
        var c = BuildConnector(out _, out _);
        Assert.Equal("ib", c.Venue);
        Assert.Equal(MarketDataSessionPolicy.SingleSession, c.SessionPolicy);
    }

    [Fact]
    public void InstrumentScale_UsesConfiguredThenDefault()
    {
        var opts = new IbDataPlaneOptions
        {
            InstrumentScales = { ["AAPL"] = new TickScale(2, 0) },
            DefaultScale = new TickScale(4, 1),
        };
        var c = BuildConnector(out _, out _, opts);
        Assert.Equal(((sbyte)2, (sbyte)0), c.InstrumentScale("AAPL"));
        Assert.Equal(((sbyte)4, (sbyte)1), c.InstrumentScale("UNKNOWN"));
    }

    [Fact]
    public async Task Stream_MapsTickUpdate_ToScaledTradeEvent()
    {
        var opts = new IbDataPlaneOptions { InstrumentScales = { ["AAPL"] = new TickScale(2, 0) } };
        var c = BuildConnector(out var session, out _, opts);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var events = new List<IMarketEvent>();
        var pump = Task.Run(async () =>
        {
            await foreach (var ev in c.Stream(["AAPL"], cts.Token)) { events.Add(ev); break; }
        }, Ct);

        await session.WaitForSubscription(Ct);
        session.PushTrade("AAPL", new IbTradeUpdate(1_700_000_000L, 296.98, 3m));
        await pump;

        var te = Assert.IsType<TradeEvent>(events[0]);
        Assert.Equal("AAPL", te.Instrument);
        Assert.Equal(1_700_000_000_000L, te.Tick.TimestampMs);
        Assert.Equal(29_698L, te.Tick.Price);
        Assert.Equal(3L, te.Tick.Quantity);
        Assert.Equal(AggressorSide.Unknown, te.Tick.Aggressor);
    }

    private static IbVenueConnector BuildConnector(out FakeIbSession session, out IIbContractResolver resolver,
        IbDataPlaneOptions? opts = null)
    {
        session = new FakeIbSession();
        resolver = Substitute.For<IIbContractResolver>();

        var aaplSpec = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");
        var aaplResolved = new ResolvedIbContract(aaplSpec, ConId: 265598, LocalSymbol: "AAPL", LastTradeDate: "");
        resolver.Resolve(Arg.Any<IbContract>(), Arg.Any<CancellationToken>()).Returns(aaplResolved);

        var aapl = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };
        var assetResolver = Substitute.For<IIbInstrumentAssetResolver>();
        assetResolver.Resolve(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<Asset>(aapl));

        return new IbVenueConnector(session, resolver, assetResolver, opts ?? new IbDataPlaneOptions());
    }
}
