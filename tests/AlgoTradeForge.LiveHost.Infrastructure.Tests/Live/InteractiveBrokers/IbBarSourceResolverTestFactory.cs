using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using NSubstitute;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

internal static class IbBarSourceResolverTestFactory
{
    public static IbBarSourceResolver Create()
    {
        var session = new FakeIbSession();
        var contractResolver = Substitute.For<IIbContractResolver>();
        var replaySource = Substitute.For<IReplaySource>();
        var backfill = Substitute.For<IBackfillRequester>();
        var warmupLoader = Substitute.For<IInt64BarLoader>();

        var options = new IbDataPlaneOptions();
        var catchupOptions = new CatchupOptions
        {
            RelayKeyPrefix = "live-md",
            DataRoot = Path.GetTempPath(),
        };

        return new IbBarSourceResolver(session, contractResolver, replaySource, backfill, warmupLoader, options, catchupOptions);
    }
}
