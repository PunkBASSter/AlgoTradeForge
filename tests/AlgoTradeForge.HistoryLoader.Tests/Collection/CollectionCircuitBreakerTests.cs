using AlgoTradeForge.HistoryLoader.Application.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class CollectionCircuitBreakerTests
{
    private readonly ICollectionCircuitBreaker _breaker =
        new CollectionCircuitBreaker(NullLogger<CollectionCircuitBreaker>.Instance);

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

    [Fact]
    public void Trip_DefaultReason_IsBan()
    {
        _breaker.Trip("IP banned");

        Assert.Equal(TripReason.Ban, _breaker.Reason);
    }

    [Fact]
    public void Trip_WithNetworkReason_SetsReasonToNetwork()
    {
        _breaker.Trip("DNS failure", TripReason.Network);

        Assert.Equal(TripReason.Network, _breaker.Reason);
    }

    [Fact]
    public void Trip_NetworkThenBan_UpgradesToBan()
    {
        _breaker.Trip("DNS failure", TripReason.Network);
        Assert.Equal(TripReason.Network, _breaker.Reason);

        _breaker.Trip("IP banned", TripReason.Ban);

        Assert.Equal(TripReason.Ban, _breaker.Reason);
    }

    [Fact]
    public void Trip_BanThenNetwork_StaysBan()
    {
        _breaker.Trip("IP banned", TripReason.Ban);
        _breaker.Trip("DNS failure", TripReason.Network);

        Assert.Equal(TripReason.Ban, _breaker.Reason);
    }

    [Fact]
    public void IsAutoResettable_NetworkTrip_ReturnsTrue()
    {
        _breaker.Trip("DNS failure", TripReason.Network);

        Assert.True(_breaker.IsAutoResettable);
    }

    [Fact]
    public void IsAutoResettable_BanTrip_ReturnsFalse()
    {
        _breaker.Trip("IP banned", TripReason.Ban);

        Assert.False(_breaker.IsAutoResettable);
    }

    [Fact]
    public void Reset_ClearsReason()
    {
        _breaker.Trip("DNS failure", TripReason.Network);
        _breaker.Reset();

        Assert.Null(_breaker.Reason);
        Assert.False(_breaker.IsAutoResettable);
    }

    [Fact]
    public void Reason_WhenNotTripped_ReturnsNull()
    {
        Assert.Null(_breaker.Reason);
    }
}
