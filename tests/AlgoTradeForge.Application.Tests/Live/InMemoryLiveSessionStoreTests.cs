using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain.Live;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Live;

public class InMemoryLiveSessionStoreTests
{
    private readonly InMemoryLiveSessionStore _store = new();

    private static SessionDetails MakeDetails(string account, ILiveConnector connector, string fingerprint = "fp-default") =>
        new(account, connector, "TestStrategy", "1.0", "Binance", "BTCUSDT", fingerprint, DateTimeOffset.UtcNow);

    [Fact]
    public void TryAdd_And_Get_ReturnsSameConnector()
    {
        var id = Guid.NewGuid();
        var connector = Substitute.For<ILiveConnector>();

        Assert.True(_store.TryAdd(id, MakeDetails("paper", connector, "fp-1")));
        var entry = _store.Get(id);

        Assert.NotNull(entry);
        Assert.Same(connector, entry.Connector);
        Assert.Equal("paper", entry.AccountName);
    }

    [Fact]
    public void Get_NonExistent_ReturnsNull()
    {
        Assert.Null(_store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Remove_ExistingSession_ReturnsTrue()
    {
        var id = Guid.NewGuid();
        _store.TryAdd(id, MakeDetails("paper", Substitute.For<ILiveConnector>(), "fp-2"));

        Assert.True(_store.Remove(id));
        Assert.Null(_store.Get(id));
    }

    [Fact]
    public void Remove_NonExistent_ReturnsFalse()
    {
        Assert.False(_store.Remove(Guid.NewGuid()));
    }

    [Fact]
    public void GetActiveSessionIds_ReturnsAllIds()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _store.TryAdd(id1, MakeDetails("paper", Substitute.For<ILiveConnector>(), "fp-3"));
        _store.TryAdd(id2, MakeDetails("live", Substitute.For<ILiveConnector>(), "fp-4"));

        var ids = _store.GetActiveSessionIds();

        Assert.Equal(2, ids.Count);
        Assert.Contains(id1, ids);
        Assert.Contains(id2, ids);
    }

    [Fact]
    public void GetActiveSessionIds_Empty_ReturnsEmpty()
    {
        Assert.Empty(_store.GetActiveSessionIds());
    }

    [Fact]
    public void TryAdd_DuplicateFingerprint_ReturnsFalse()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        Assert.True(_store.TryAdd(id1, MakeDetails("paper", Substitute.For<ILiveConnector>(), "same-fp")));
        Assert.False(_store.TryAdd(id2, MakeDetails("paper", Substitute.For<ILiveConnector>(), "same-fp")));
        Assert.Null(_store.Get(id2));
    }

    [Fact]
    public void TryAdd_SameFingerprint_AllowedAfterRemove()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        _store.TryAdd(id1, MakeDetails("paper", Substitute.For<ILiveConnector>(), "reuse-fp"));
        _store.Remove(id1);

        Assert.True(_store.TryAdd(id2, MakeDetails("paper", Substitute.For<ILiveConnector>(), "reuse-fp")));
        Assert.NotNull(_store.Get(id2));
    }

    [Fact]
    public void TryAdd_DifferentFingerprints_BothSucceed()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        Assert.True(_store.TryAdd(id1, MakeDetails("paper", Substitute.For<ILiveConnector>(), "fp-a")));
        Assert.True(_store.TryAdd(id2, MakeDetails("paper", Substitute.For<ILiveConnector>(), "fp-b")));
    }
}
