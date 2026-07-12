using AlgoTradeForge.HistoryLoader.Application.Jobs;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Jobs;

public sealed class JobCancellationMapTests
{
    [Fact]
    public void Trip_CancelsRegisteredToken()
    {
        var map = new JobCancellationMap();
        var token = map.Register("j1", CancellationToken.None);
        Assert.False(token.IsCancellationRequested);
        map.Trip("j1");
        Assert.True(token.IsCancellationRequested);
        map.Remove("j1");   // idempotent, disposes CTS
    }

    [Fact]
    public void Trip_UnknownJob_IsNoOp()
    {
        var map = new JobCancellationMap();
        // must not throw
        map.Trip("nope");
    }

    [Fact]
    public void Remove_IsIdempotent_AndDisposes()
    {
        var map = new JobCancellationMap();
        map.Register("j1", CancellationToken.None);
        map.Remove("j1");
        // second Remove must not throw
        map.Remove("j1");
        // fresh Register after removal returns a non-cancelled token (proving old CTS was not reused)
        var token = map.Register("j1", CancellationToken.None);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void Register_SameJobTwice_ReturnsSameToken()
    {
        var map = new JobCancellationMap();
        var token1 = map.Register("j1", CancellationToken.None);
        var token2 = map.Register("j1", CancellationToken.None);
        Assert.Equal(token1, token2);
    }

    [Fact]
    public void Trip_AfterConcurrentRemove_DoesNotThrow()
    {
        var map = new JobCancellationMap();
        map.Register("j1", CancellationToken.None);
        map.Remove("j1");   // disposes the CTS
        // TryGetValue returns false — must still not throw
        map.Trip("j1");
    }

    [Fact]
    public void Register_LinkedToken_CancelsWhenHostTokenCancels()
    {
        var map = new JobCancellationMap();
        using var host = new CancellationTokenSource();
        var tok = map.Register("j1", host.Token);
        Assert.False(tok.IsCancellationRequested);
        host.Cancel();
        Assert.True(tok.IsCancellationRequested);
    }
}
