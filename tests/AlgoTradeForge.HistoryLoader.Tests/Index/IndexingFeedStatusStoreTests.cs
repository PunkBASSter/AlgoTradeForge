using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class IndexingFeedStatusStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Save_DelegatesThenEnqueuesFeedTouched()
    {
        var inner = Substitute.For<IFeedStatusStore>();
        var maintenance = Substitute.For<IIndexMaintenance>();
        var store = new IndexingFeedStatusStore(inner, maintenance);
        var status = new FeedStatus { FeedName = "candles", Interval = "1h" };

        await store.Save(@"C:\data\binance\BTCUSDT", "candles", "1h", status, Ct);

        await inner.Received(1).Save(@"C:\data\binance\BTCUSDT", "candles", "1h", status, Arg.Any<CancellationToken>());
        maintenance.Received(1).Enqueue(Arg.Is<IndexWork>(w =>
            w is IndexWork.FeedTouched &&
            ((IndexWork.FeedTouched)w).FeedName == "candles" &&
            ((IndexWork.FeedTouched)w).Interval == "1h"));
    }

    [Fact]
    public async Task Load_DelegatesWithoutEnqueue()
    {
        var inner = Substitute.For<IFeedStatusStore>();
        var maintenance = Substitute.For<IIndexMaintenance>();
        var store = new IndexingFeedStatusStore(inner, maintenance);

        await store.Load("dir", "candles", "1h", Ct);

        maintenance.DidNotReceiveWithAnyArgs().Enqueue(default!);
    }

    [Fact]
    public async Task Update_DelegatesThenEnqueuesFeedTouched()
    {
        var inner = Substitute.For<IFeedStatusStore>();
        var maintenance = Substitute.For<IIndexMaintenance>();
        var store = new IndexingFeedStatusStore(inner, maintenance);
        Func<FeedStatus?, FeedStatus> mutate = _ => new FeedStatus { FeedName = "candles", Interval = "1h" };

        await store.Update(@"C:\data\binance\BTCUSDT", "candles", "1h", mutate, Ct);

        await inner.Received(1).Update(@"C:\data\binance\BTCUSDT", "candles", "1h", mutate, Arg.Any<CancellationToken>());
        maintenance.Received(1).Enqueue(Arg.Is<IndexWork>(w =>
            w is IndexWork.FeedTouched &&
            ((IndexWork.FeedTouched)w).FeedName == "candles" &&
            ((IndexWork.FeedTouched)w).Interval == "1h"));
    }
}
