using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public sealed class IbConnectionOrderIdTests
{
    [Fact]
    public void NextOrderId_StartsFromSeed_AndIncrementsMonotonically()
    {
        var conn = IbConnectionTestFactory.WithSeededNextValidId(5000);
        Assert.Equal(5000, conn.NextOrderId());
        Assert.Equal(5001, conn.NextOrderId());
        Assert.Equal(5002, conn.NextOrderId());
    }

    [Fact]
    public void SeedNextOrderId_ReArmsToLargerServerValue_OnReconnect()
    {
        var conn = IbConnectionTestFactory.WithSeededNextValidId(5000);
        conn.NextOrderId(); // 5000
        conn.SeedNextOrderId(9000); // reconnect hands back a higher seed
        Assert.Equal(9000, conn.NextOrderId());
    }

    [Fact]
    public void SeedNextOrderId_IgnoresSmallerSeed_NoRewind()
    {
        var conn = IbConnectionTestFactory.WithSeededNextValidId(5000);
        conn.NextOrderId(); // 5000, counter now at 5001
        conn.SeedNextOrderId(100); // stale smaller seed — must be ignored
        Assert.Equal(5001, conn.NextOrderId());
    }
}
