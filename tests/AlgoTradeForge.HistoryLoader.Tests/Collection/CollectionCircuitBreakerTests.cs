using AlgoTradeForge.HistoryLoader.Application.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class CollectionCircuitBreakerTests
{
    private readonly CollectionCircuitBreaker _breaker =
        new(NullLogger<CollectionCircuitBreaker>.Instance);

    [Fact]
    public void IsTripped_InitiallyFalse()
    {
        Assert.False(_breaker.IsTripped);
    }

    [Fact]
    public void Trip_SetsIsTripped()
    {
        _breaker.Trip("test ban");

        Assert.True(_breaker.IsTripped);
    }

    [Fact]
    public void Trip_IsIdempotent()
    {
        _breaker.Trip("first");
        _breaker.Trip("second");

        Assert.True(_breaker.IsTripped);
    }

    [Fact]
    public void Reset_ClearsTripped()
    {
        _breaker.Trip("test ban");
        Assert.True(_breaker.IsTripped);

        _breaker.Reset();

        Assert.False(_breaker.IsTripped);
    }
}
