using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.Exit;

public sealed class TimeBasedExitRuleTests
{
    private static readonly StrategyContext DefaultContext = new();

    private static OrderGroup CreateGroup(DateTimeOffset createdAt) => new()
    {
        GroupId = 1,
        EntrySide = OrderSide.Buy,
        EntryQuantity = 1m,
        Asset = TestAssets.BtcUsdt,
        CreatedAt = createdAt,
    };

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        var rule = new TimeBasedExitRule(maxHoldBars: 10, barIntervalMs: 60_000);

        Assert.Equal("TimeBased", rule.Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void Evaluate_BeforeMaxHoldBars_ReturnsZero(int barsElapsed)
    {
        const int maxHold = 10;
        const long intervalMs = 60_000; // 1 min bars
        var createdAt = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var group = CreateGroup(createdAt);
        var rule = new TimeBasedExitRule(maxHold, intervalMs);

        var barTimestampMs = createdAt.ToUnixTimeMilliseconds() + barsElapsed * intervalMs;
        var bar = TestBars.Flat(timestampMs: barTimestampMs);

        Assert.Equal(0, rule.Evaluate(bar, DefaultContext, group));
    }

    [Fact]
    public void Evaluate_AtExactlyMaxHoldBars_ReturnsNeg100()
    {
        const int maxHold = 10;
        const long intervalMs = 60_000;
        var createdAt = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var group = CreateGroup(createdAt);
        var rule = new TimeBasedExitRule(maxHold, intervalMs);

        var barTimestampMs = createdAt.ToUnixTimeMilliseconds() + maxHold * intervalMs;
        var bar = TestBars.Flat(timestampMs: barTimestampMs);

        Assert.Equal(-100, rule.Evaluate(bar, DefaultContext, group));
    }

    [Fact]
    public void Evaluate_PastMaxHoldBars_ReturnsNeg100()
    {
        const int maxHold = 5;
        const long intervalMs = 60_000;
        var createdAt = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var group = CreateGroup(createdAt);
        var rule = new TimeBasedExitRule(maxHold, intervalMs);

        var barTimestampMs = createdAt.ToUnixTimeMilliseconds() + (maxHold + 3) * intervalMs;
        var bar = TestBars.Flat(timestampMs: barTimestampMs);

        Assert.Equal(-100, rule.Evaluate(bar, DefaultContext, group));
    }

    [Fact]
    public void Evaluate_ZeroBarsElapsed_ReturnsZero()
    {
        const int maxHold = 10;
        const long intervalMs = 60_000;
        var createdAt = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var group = CreateGroup(createdAt);
        var rule = new TimeBasedExitRule(maxHold, intervalMs);

        var bar = TestBars.Flat(timestampMs: createdAt.ToUnixTimeMilliseconds());

        Assert.Equal(0, rule.Evaluate(bar, DefaultContext, group));
    }
}
