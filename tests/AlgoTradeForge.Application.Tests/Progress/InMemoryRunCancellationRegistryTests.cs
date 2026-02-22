using AlgoTradeForge.Application.Progress;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Progress;

public sealed class InMemoryRunCancellationRegistryTests
{
    private readonly InMemoryRunCancellationRegistry _registry = new();

    [Fact]
    public void Register_And_TryGetToken_Returns_Token()
    {
        var id = Guid.NewGuid();
        var cts = new CancellationTokenSource();

        _registry.Register(id, cts);
        var token = _registry.TryGetToken(id);

        Assert.NotNull(token);
        Assert.False(token.Value.IsCancellationRequested);
    }

    [Fact]
    public void TryGetToken_Returns_Null_For_Unknown_Id()
    {
        var token = _registry.TryGetToken(Guid.NewGuid());

        Assert.Null(token);
    }

    [Fact]
    public void TryCancel_Returns_True_And_Triggers_Cancellation()
    {
        var id = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        _registry.Register(id, cts);

        var result = _registry.TryCancel(id);

        Assert.True(result);
        Assert.True(cts.Token.IsCancellationRequested);
    }

    [Fact]
    public void TryCancel_Returns_False_For_Unknown_Id()
    {
        var result = _registry.TryCancel(Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public void Remove_Cleans_Up()
    {
        var id = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        _registry.Register(id, cts);

        _registry.Remove(id);

        Assert.Null(_registry.TryGetToken(id));
        Assert.False(_registry.TryCancel(id));
    }

    [Fact]
    public void Concurrent_Register_And_TryCancel_Are_ThreadSafe()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();
        var sources = ids.Select(_ => new CancellationTokenSource()).ToList();

        // Register concurrently
        Parallel.ForEach(Enumerable.Range(0, 100), i =>
        {
            _registry.Register(ids[i], sources[i]);
        });

        // Cancel concurrently
        var cancelResults = new bool[100];
        Parallel.ForEach(Enumerable.Range(0, 100), i =>
        {
            cancelResults[i] = _registry.TryCancel(ids[i]);
        });

        Assert.All(cancelResults, Assert.True);
        Assert.All(sources, s => Assert.True(s.Token.IsCancellationRequested));
    }
}
