using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.Infrastructure.Collection;
using AlgoTradeForge.Storage;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Collection;

public class CollectionConfigStoreTests
{
    private const string Key = "collection.json";

    [Fact]
    public async Task Load_returns_empty_config_and_null_etag_when_file_absent()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = Substitute.For<IFileStorage>();
        storage.ReadWithEtag(Key, Arg.Any<CancellationToken>()).Returns((StoredObject?)null);
        var store = new CollectionConfigStore(storage);

        var result = await store.Load(ct);

        Assert.Empty(result.Config.Feeds);
        Assert.Null(result.ETag);
    }

    [Fact]
    public async Task Save_then_Load_round_trips_polymorphic_subscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = Substitute.For<IFileStorage>();
        string? captured = null;
        storage.WriteIfMatch(Key, Arg.Do<string>(s => captured = s), Arg.Is<string?>(e => e == null), Arg.Any<CancellationToken>())
            .Returns("etag-1");
        var store = new CollectionConfigStore(storage);

        var config = new CollectionConfig(
        [
            new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary),
            new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, AlgoTradeForge.Domain.Strategy.TimeFrame.Parse("1m")),
        ]);

        var etag = await store.Save(config, expectedETag: null, ct);
        Assert.Equal("etag-1", etag);
        Assert.NotNull(captured);

        // Feed the captured JSON back through Load to prove the polymorphic round-trip.
        storage.ReadWithEtag(Key, Arg.Any<CancellationToken>())
            .Returns(new StoredObject(captured!, "etag-1"));
        var loaded = await store.Load(ct);

        Assert.Equal(2, loaded.Config.Feeds.Count);
        Assert.IsType<TickSubscription>(loaded.Config.Feeds[0]);
        var tb = Assert.IsType<TimeBarSubscription>(loaded.Config.Feeds[1]);
        Assert.Equal("1m", tb.TimeFrame.Code);
        Assert.Equal("etag-1", loaded.ETag);
    }

    [Fact]
    public async Task Save_propagates_ConcurrencyConflictException_on_stale_etag()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = Substitute.For<IFileStorage>();
        storage.WriteIfMatch(Key, Arg.Any<string>(), "stale", Arg.Any<CancellationToken>())
            .Throws(new ConcurrencyConflictException(Key, "stale", "current"));
        var store = new CollectionConfigStore(storage);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.Save(new CollectionConfig([]), expectedETag: "stale", ct));
    }
}
