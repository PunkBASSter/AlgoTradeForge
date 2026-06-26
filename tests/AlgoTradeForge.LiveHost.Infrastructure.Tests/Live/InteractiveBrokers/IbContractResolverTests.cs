using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public class IbContractResolverTests
{
    private static IbContract Spec(string symbol = "AAPL") =>
        new(symbol, IbSecType.Stk, "SMART", "NASDAQ", "USD");

    [Fact]
    public async Task Resolve_CacheMiss_FetchesAndReturns()
    {
        var spec = Spec();
        var client = Substitute.For<IIbContractDetailsClient>();
        client.FetchContractDetails(spec, Arg.Any<CancellationToken>())
            .Returns(new ResolvedIbContract(spec, 265598, "AAPL", ""));
        var resolver = new IbContractResolver(client);

        var resolved = await resolver.Resolve(spec, TestContext.Current.CancellationToken);

        Assert.Equal(265598, resolved.ConId);
        await client.Received(1).FetchContractDetails(spec, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_CacheHit_DoesNotRefetch()
    {
        var spec = Spec();
        var client = Substitute.For<IIbContractDetailsClient>();
        client.FetchContractDetails(spec, Arg.Any<CancellationToken>())
            .Returns(new ResolvedIbContract(spec, 265598, "AAPL", ""));
        var resolver = new IbContractResolver(client);

        var first = await resolver.Resolve(spec, TestContext.Current.CancellationToken);
        var second = await resolver.Resolve(spec, TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        await client.Received(1).FetchContractDetails(spec, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_DistinctSpecs_FetchedIndependently()
    {
        var a = Spec("AAPL");
        var b = Spec("MSFT");
        var client = Substitute.For<IIbContractDetailsClient>();
        client.FetchContractDetails(a, Arg.Any<CancellationToken>()).Returns(new ResolvedIbContract(a, 1, "AAPL", ""));
        client.FetchContractDetails(b, Arg.Any<CancellationToken>()).Returns(new ResolvedIbContract(b, 2, "MSFT", ""));
        var resolver = new IbContractResolver(client);

        Assert.Equal(1, (await resolver.Resolve(a, TestContext.Current.CancellationToken)).ConId);
        Assert.Equal(2, (await resolver.Resolve(b, TestContext.Current.CancellationToken)).ConId);
    }
}
