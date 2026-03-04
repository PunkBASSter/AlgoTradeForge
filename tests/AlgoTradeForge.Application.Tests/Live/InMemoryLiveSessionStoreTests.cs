using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain.Live;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Live;

public class InMemoryLiveSessionStoreTests
{
    private readonly InMemoryLiveSessionStore _store = new();

    [Fact]
    public void Add_And_Get_ReturnsSameConnector()
    {
        var id = Guid.NewGuid();
        var connector = Substitute.For<ILiveConnector>();

        _store.Add(id, connector);
        var retrieved = _store.Get(id);

        Assert.Same(connector, retrieved);
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
        _store.Add(id, Substitute.For<ILiveConnector>());

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
        _store.Add(id1, Substitute.For<ILiveConnector>());
        _store.Add(id2, Substitute.For<ILiveConnector>());

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
}
