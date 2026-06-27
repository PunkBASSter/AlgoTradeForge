using AlgoTradeForge.Domain;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class OrderRouterTests
{
    private static readonly Asset TestAsset =
        CryptoAsset.Create("BTCUSDT", "Binance", 2, 0.00001m, 9000m, 0.00001m);

    private static IAccountTarget FakeTarget(string name)
    {
        var t = Substitute.For<IAccountTarget>();
        t.AccountName.Returns(name);
        return t;
    }

    [Fact]
    public async Task ResolveTarget_GetOrCreate_CreatesOncePerAccount_UnderConcurrency()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Substitute.For<IAccountTargetFactory>();
        factory.Create("A", Arg.Any<Asset>(), Arg.Any<CancellationToken>()).Returns(_ => FakeTarget("A"));
        var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

        var tasks = Enumerable.Range(0, 16).Select(_ => router.ResolveTarget("A", TestAsset, ct)).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Same(results[0], r));
        await factory.Received(1).Create("A", Arg.Any<Asset>(), Arg.Any<CancellationToken>());
        // The execution asset is threaded through to the factory.
        await factory.Received(1).Create("A", TestAsset, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseTarget_DisposesTarget_OnlyOnLastRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = FakeTarget("A");
        var factory = Substitute.For<IAccountTargetFactory>();
        factory.Create("A", Arg.Any<Asset>(), Arg.Any<CancellationToken>()).Returns(target);
        var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

        await router.ResolveTarget("A", TestAsset, ct);   // refcount 1
        await router.ResolveTarget("A", TestAsset, ct);   // refcount 2 (same target)

        await router.ReleaseTarget("A", ct);   // -> 1, not disposed
        await target.DidNotReceive().DisposeAsync();

        await router.ReleaseTarget("A", ct);   // -> 0, disposed
        await target.Received(1).DisposeAsync();
        Assert.Empty(router.Targets);
    }

    [Fact]
    public async Task ResolveTarget_AfterDispose_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Substitute.For<IAccountTargetFactory>();
        factory.Create("A", Arg.Any<Asset>(), Arg.Any<CancellationToken>()).Returns(_ => FakeTarget("A"));
        var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

        await router.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => router.ResolveTarget("A", TestAsset, ct));
    }

    [Fact]
    public void TrackOrder_Then_TryResolveSession_RoundTrips()
    {
        var router = new OrderRouter(Substitute.For<IAccountTargetFactory>(), NullLogger<OrderRouter>.Instance);
        var session = Guid.NewGuid();
        router.TrackOrder(123L, session);

        Assert.True(router.TryResolveSession(123L, out var resolved));
        Assert.Equal(session, resolved);
        Assert.False(router.TryResolveSession(999L, out _));
    }

    [Fact]
    public void UntrackOrder_RemovesMapping()
    {
        var router = new OrderRouter(Substitute.For<IAccountTargetFactory>(), NullLogger<OrderRouter>.Instance);
        router.TrackOrder(55L, Guid.NewGuid());
        router.UntrackOrder(55L);
        Assert.False(router.TryResolveSession(55L, out _));
    }
}
