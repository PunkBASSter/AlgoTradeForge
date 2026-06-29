using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class DispatchMarketDataSourceTests
{
    [Fact]
    public async Task Delegates_RegisterAndEnsureSources_ToDispatchAndTickRouter()
    {
        var dispatch = Substitute.For<IStrategyDispatch>();
        var tickRouter = Substitute.For<ITickRouter>();
        var source = new DispatchMarketDataSource(dispatch, tickRouter);

        var reg = new LiveSessionRegistration(Guid.NewGuid(),
            Substitute.For<AlgoTradeForge.Domain.Strategy.IInt64BarStrategy>(),
            [], System.Threading.Channels.Channel.CreateUnbounded<Action>().Writer);

        source.Register(reg);
        Func<string, ScaleContext> scaleFor = _ => new ScaleContext(0.01m);
        await source.EnsureSources(reg, scaleFor);

        dispatch.Received(1).Register(reg);
        await tickRouter.Received(1).EnsureSources(reg, scaleFor);
    }

    [Fact]
    public void Delegates_RecentBars_ToTickRouter()
    {
        var dispatch = Substitute.For<IStrategyDispatch>();
        var tickRouter = Substitute.For<ITickRouter>();
        var source = new DispatchMarketDataSource(dispatch, tickRouter);

        var expected = new List<Int64Bar>();
        var spec = new BarSpecKey("1h");
        tickRouter.RecentBars("BTCUSDT", spec).Returns(expected);

        var result = source.RecentBars("BTCUSDT", spec);

        Assert.Same(expected, result);
        tickRouter.Received(1).RecentBars("BTCUSDT", spec);
    }

    [Fact]
    public async Task Delegates_RemoveSources_ToTickRouter()
    {
        var dispatch = Substitute.For<IStrategyDispatch>();
        var tickRouter = Substitute.For<ITickRouter>();
        var source = new DispatchMarketDataSource(dispatch, tickRouter);

        var sessionId = Guid.NewGuid();
        await source.RemoveSources(sessionId);

        await tickRouter.Received(1).RemoveSources(sessionId);
    }
}
