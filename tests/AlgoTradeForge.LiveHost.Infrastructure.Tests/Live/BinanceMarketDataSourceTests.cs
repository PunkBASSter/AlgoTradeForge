using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class BinanceMarketDataSourceTests
{
    [Fact]
    public async Task Delegates_RegisterAndEnsureSources_ToDispatchAndTickRouter()
    {
        var dispatch = Substitute.For<IStrategyDispatch>();
        var tickRouter = Substitute.For<ITickRouter>();
        var source = new BinanceMarketDataSource(dispatch, tickRouter);

        var reg = new LiveSessionRegistration(Guid.NewGuid(),
            Substitute.For<AlgoTradeForge.Domain.Strategy.IInt64BarStrategy>(),
            [], System.Threading.Channels.Channel.CreateUnbounded<Action>().Writer);

        source.Register(reg);
        Func<string, ScaleContext> scaleFor = _ => new ScaleContext(0.01m);
        await source.EnsureSources(reg, scaleFor);

        dispatch.Received(1).Register(reg);
        await tickRouter.Received(1).EnsureSources(reg, scaleFor);
    }
}
