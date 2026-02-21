using AlgoTradeForge.Application.Debug;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Debug;

public class InMemoryDebugSessionStoreTests
{
    [Fact]
    public async Task Create_ReturnsSessionWithId()
    {
        var store = new InMemoryDebugSessionStore();

        var session = store.Create("AAPL", "TestStrategy");

        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal("AAPL", session.AssetName);
        Assert.Equal("TestStrategy", session.StrategyName);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Get_ExistingSession_ReturnsIt()
    {
        var store = new InMemoryDebugSessionStore();
        var session = store.Create("AAPL", "TestStrategy");

        var retrieved = store.Get(session.Id);

        Assert.Same(session, retrieved);
        await session.DisposeAsync();
    }

    [Fact]
    public void Get_NonExistentId_ReturnsNull()
    {
        var store = new InMemoryDebugSessionStore();

        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public async Task TryRemove_ExistingSession_RemovesAndReturnsTrue()
    {
        var store = new InMemoryDebugSessionStore();
        var session = store.Create("AAPL", "TestStrategy");

        Assert.True(store.TryRemove(session.Id, out var removed));
        Assert.Same(session, removed);
        Assert.Null(store.Get(session.Id));

        await session.DisposeAsync();
    }

    [Fact]
    public void TryRemove_NonExistent_ReturnsFalse()
    {
        var store = new InMemoryDebugSessionStore();

        Assert.False(store.TryRemove(Guid.NewGuid(), out _));
    }

    [Fact]
    public async Task GetAll_ReturnsAllSessions()
    {
        var store = new InMemoryDebugSessionStore();
        var s1 = store.Create("AAPL", "Strat1");
        var s2 = store.Create("BTC-USDT", "Strat2");

        var all = store.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Id == s1.Id);
        Assert.Contains(all, s => s.Id == s2.Id);

        await s1.DisposeAsync();
        await s2.DisposeAsync();
    }

    [Fact]
    public async Task Create_ExceedsMaxSessions_Throws()
    {
        var store = new InMemoryDebugSessionStore(maxSessions: 2);
        var s1 = store.Create("A", "S1");
        var s2 = store.Create("B", "S2");

        var ex = Assert.Throws<InvalidOperationException>(() => store.Create("C", "S3"));
        Assert.Contains("Maximum", ex.Message);

        await s1.DisposeAsync();
        await s2.DisposeAsync();
    }

    [Fact]
    public async Task Create_AfterRemoval_AllowsNewSession()
    {
        var store = new InMemoryDebugSessionStore(maxSessions: 1);
        var s1 = store.Create("A", "S1");

        Assert.Throws<InvalidOperationException>(() => store.Create("B", "S2"));

        store.TryRemove(s1.Id, out _);
        await s1.DisposeAsync();

        var s2 = store.Create("B", "S2");
        Assert.NotNull(store.Get(s2.Id));
        await s2.DisposeAsync();
    }
}
