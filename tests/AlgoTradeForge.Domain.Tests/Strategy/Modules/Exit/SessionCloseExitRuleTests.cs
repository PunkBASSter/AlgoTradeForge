using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.Exit;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.Exit;

public sealed class SessionCloseExitRuleTests
{
    private static readonly StrategyContext DefaultContext = new();

    private static OrderGroup CreateGroup() => new()
    {
        GroupId = 1,
        EntrySide = OrderSide.Buy,
        EntryQuantity = 1m,
        Asset = TestAssets.BtcUsdt,
    };

    [Fact]
    public void Name_ReturnsExpectedValue()
    {
        var rule = new SessionCloseExitRule(closeHourUtc: 16);
        Assert.Equal("SessionClose", rule.Name);
    }

    [Fact]
    public void Evaluate_AtCloseHour_ReturnsNeg100()
    {
        var rule = new SessionCloseExitRule(closeHourUtc: 16);

        // Bar at 16:00 UTC
        var ts = new DateTimeOffset(2024, 1, 15, 16, 0, 0, TimeSpan.Zero);
        var bar = TestBars.Flat(timestampMs: ts.ToUnixTimeMilliseconds());

        Assert.Equal(-100, rule.Evaluate(bar, DefaultContext, CreateGroup()));
    }

    [Fact]
    public void Evaluate_BeforeCloseHour_ReturnsZero()
    {
        var rule = new SessionCloseExitRule(closeHourUtc: 16);

        // Bar at 15:30 UTC
        var ts = new DateTimeOffset(2024, 1, 15, 15, 30, 0, TimeSpan.Zero);
        var bar = TestBars.Flat(timestampMs: ts.ToUnixTimeMilliseconds());

        Assert.Equal(0, rule.Evaluate(bar, DefaultContext, CreateGroup()));
    }

    [Fact]
    public void Evaluate_AfterCloseHour_ReturnsZero()
    {
        var rule = new SessionCloseExitRule(closeHourUtc: 16);

        // Bar at 17:00 UTC
        var ts = new DateTimeOffset(2024, 1, 15, 17, 0, 0, TimeSpan.Zero);
        var bar = TestBars.Flat(timestampMs: ts.ToUnixTimeMilliseconds());

        Assert.Equal(0, rule.Evaluate(bar, DefaultContext, CreateGroup()));
    }

    [Fact]
    public void Evaluate_MidnightClose_Works()
    {
        var rule = new SessionCloseExitRule(closeHourUtc: 0);

        var ts = new DateTimeOffset(2024, 1, 15, 0, 15, 0, TimeSpan.Zero);
        var bar = TestBars.Flat(timestampMs: ts.ToUnixTimeMilliseconds());

        Assert.Equal(-100, rule.Evaluate(bar, DefaultContext, CreateGroup()));
    }

    [Fact]
    public void Evaluate_LateMinuteInCloseHour_StillReturnsNeg100()
    {
        var rule = new SessionCloseExitRule(closeHourUtc: 16);

        // Bar at 16:59 UTC — still in the close hour
        var ts = new DateTimeOffset(2024, 1, 15, 16, 59, 0, TimeSpan.Zero);
        var bar = TestBars.Flat(timestampMs: ts.ToUnixTimeMilliseconds());

        Assert.Equal(-100, rule.Evaluate(bar, DefaultContext, CreateGroup()));
    }
}
