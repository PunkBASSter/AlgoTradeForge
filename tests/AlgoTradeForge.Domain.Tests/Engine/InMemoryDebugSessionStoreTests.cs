using AlgoTradeForge.Application.Debug;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class InMemoryDebugSessionStoreTests
{
    [Fact]
    public void Create_ReturnsSessionWithId()
    {
        var store = new InMemoryDebugSessionStore();

        var session = store.Create("AAPL", "TestStrategy");

        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal("AAPL", session.AssetName);
        Assert.Equal("TestStrategy", session.StrategyName);

        session.Dispose();
    }

    [Fact]
    public void Get_ExistingSession_ReturnsIt()
    {
        var store = new InMemoryDebugSessionStore();
        var session = store.Create("AAPL", "TestStrategy");

        var retrieved = store.Get(session.Id);

        Assert.Same(session, retrieved);
        session.Dispose();
    }

    [Fact]
    public void Get_NonExistentId_ReturnsNull()
    {
        var store = new InMemoryDebugSessionStore();

        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void TryRemove_ExistingSession_RemovesAndReturnsTrue()
    {
        var store = new InMemoryDebugSessionStore();
        var session = store.Create("AAPL", "TestStrategy");

        Assert.True(store.TryRemove(session.Id, out var removed));
        Assert.Same(session, removed);
        Assert.Null(store.Get(session.Id));

        session.Dispose();
    }

    [Fact]
    public void TryRemove_NonExistent_ReturnsFalse()
    {
        var store = new InMemoryDebugSessionStore();

        Assert.False(store.TryRemove(Guid.NewGuid(), out _));
    }

    [Fact]
    public void GetAll_ReturnsAllSessions()
    {
        var store = new InMemoryDebugSessionStore();
        var s1 = store.Create("AAPL", "Strat1");
        var s2 = store.Create("BTC-USDT", "Strat2");

        var all = store.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Id == s1.Id);
        Assert.Contains(all, s => s.Id == s2.Id);

        s1.Dispose();
        s2.Dispose();
    }

    [Fact]
    public void Create_ExceedsMaxSessions_Throws()
    {
        var store = new InMemoryDebugSessionStore(maxSessions: 2);
        var s1 = store.Create("A", "S1");
        var s2 = store.Create("B", "S2");

        var ex = Assert.Throws<InvalidOperationException>(() => store.Create("C", "S3"));
        Assert.Contains("Maximum", ex.Message);

        s1.Dispose();
        s2.Dispose();
    }

    [Fact]
    public void Create_AfterRemoval_AllowsNewSession()
    {
        var store = new InMemoryDebugSessionStore(maxSessions: 1);
        var s1 = store.Create("A", "S1");

        Assert.Throws<InvalidOperationException>(() => store.Create("B", "S2"));

        store.TryRemove(s1.Id, out _);
        s1.Dispose();

        var s2 = store.Create("B", "S2");
        Assert.NotNull(store.Get(s2.Id));
        s2.Dispose();
    }
}
