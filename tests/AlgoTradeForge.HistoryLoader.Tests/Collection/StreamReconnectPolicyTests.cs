using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Tests.TestHelpers;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class StreamReconnectPolicyTests
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StableUptime = TimeSpan.FromMinutes(1);

    private readonly TestClock _clock = new(new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero));

    private StreamReconnectPolicy CreatePolicy(int maxAttempts = 10) =>
        new(maxAttempts, InitialDelay, StableUptime, _clock);

    [Fact]
    public void ConsecutiveFailures_EscalateAttemptAndDelay()
    {
        var policy = CreatePolicy();

        var first = policy.OnFailure();
        var second = policy.OnFailure();
        var third = policy.OnFailure();

        Assert.Equal(new ReconnectDecision(1, false, TimeSpan.FromSeconds(5)), first);
        Assert.Equal(new ReconnectDecision(2, false, TimeSpan.FromSeconds(10)), second);
        Assert.Equal(new ReconnectDecision(3, false, TimeSpan.FromSeconds(20)), third);
    }

    [Fact]
    public void FailureAfterStableUptime_StartsNewSeries()
    {
        var policy = CreatePolicy();
        for (var i = 0; i < 9; i++)
            policy.OnFailure();

        policy.OnConnected();
        _clock.Advance(TimeSpan.FromMinutes(2));
        var decision = policy.OnFailure();

        Assert.Equal(new ReconnectDecision(1, false, TimeSpan.FromSeconds(5)), decision);
    }

    [Fact]
    public void FailureBeforeStableUptime_ContinuesSeries()
    {
        var policy = CreatePolicy();
        policy.OnFailure();

        policy.OnConnected();
        _clock.Advance(TimeSpan.FromSeconds(10));
        var decision = policy.OnFailure();

        Assert.Equal(2, decision.Attempt);
    }

    [Fact]
    public void ExceedingMaxAttempts_GivesUp()
    {
        var policy = CreatePolicy(maxAttempts: 10);
        for (var i = 0; i < 10; i++)
            Assert.False(policy.OnFailure().GiveUp);

        var decision = policy.OnFailure();

        Assert.True(decision.GiveUp);
        Assert.Equal(11, decision.Attempt);
    }

    [Fact]
    public void RapidConnectDropCycles_StillGiveUp()
    {
        var policy = CreatePolicy(maxAttempts: 10);

        for (var i = 0; i < 10; i++)
        {
            policy.OnConnected();
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(policy.OnFailure().GiveUp);
        }

        policy.OnConnected();
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(policy.OnFailure().GiveUp);
    }

    [Fact]
    public void StableConnectionCycles_NeverGiveUp()
    {
        var policy = CreatePolicy(maxAttempts: 10);

        for (var i = 0; i < 20; i++)
        {
            policy.OnConnected();
            _clock.Advance(TimeSpan.FromHours(1));
            var decision = policy.OnFailure();

            Assert.False(decision.GiveUp);
            Assert.Equal(1, decision.Attempt);
        }
    }

    [Fact]
    public void Reset_ClearsSeries()
    {
        var policy = CreatePolicy();
        for (var i = 0; i < 5; i++)
            policy.OnFailure();

        policy.Reset();
        var decision = policy.OnFailure();

        Assert.Equal(1, decision.Attempt);
    }
}
