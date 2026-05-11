using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Backtests;

/// <summary>
/// P4-11 — Pins <see cref="BacktestInputs"/>'s constructor invariants: <c>Subscriptions[0]</c>
/// is the primary (drives the bar clock); subsequent entries must have <c>Role=Side</c>.
/// Loud failures at the boundary beat silent index-shuffle behavior at engine time.
/// </summary>
public class BacktestInputsTests
{
    private static TimeBarSubscription PrimaryTimeBar() =>
        new("BTC", "ex", DataFeedRole.Primary, new TimeFrame(TimeSpan.FromMinutes(1)));

    private static AltBarSubscription PrimaryAltBar() =>
        new("BTC", "ex", DataFeedRole.Primary, "EqV_1m_500m");

    private static TickSubscription PrimaryTick() =>
        new("BTC", "ex", DataFeedRole.Primary);

    private static SideFeedSubscription Side(string feedId = "funding-rate") =>
        new("BTC", "ex", DataFeedRole.Side, feedId);

    [Fact]
    public void Construct_WithTimeBarPrimaryAndNoSideFeeds_Succeeds()
    {
        var inputs = new BacktestInputs([PrimaryTimeBar()]);

        Assert.IsType<TimeBarSubscription>(inputs.Subscriptions[0]);
        Assert.Single(inputs.Subscriptions);
    }

    [Fact]
    public void Construct_WithAltBarPrimary_Succeeds()
    {
        var inputs = new BacktestInputs([PrimaryAltBar()]);

        Assert.IsType<AltBarSubscription>(inputs.Subscriptions[0]);
    }

    [Fact]
    public void Construct_WithTickPrimary_Succeeds()
    {
        // Ticks are a valid primary source (P2a-6 monotonic-bump path); the bar clock
        // ticks per raw trade. Validation should not reject this kind.
        var inputs = new BacktestInputs([PrimaryTick()]);

        Assert.IsType<TickSubscription>(inputs.Subscriptions[0]);
    }

    [Fact]
    public void Construct_WithSidePrimary_Throws()
    {
        // SideFeedSubscription forbids Role=Primary in its own ctor (it's the single-instance
        // invariant), so we can't synthesize a SideFeedSubscription with Role=Primary here.
        // But we CAN construct a TimeBarSubscription with Role=Side and try to pass it as
        // primary — that's the cross-instance invariant BacktestInputs enforces.
        var sideRoleAsPrimary = new TimeBarSubscription("BTC", "ex", DataFeedRole.Side,
            new TimeFrame(TimeSpan.FromMinutes(1)));

        var ex = Assert.Throws<ArgumentException>(() => new BacktestInputs([sideRoleAsPrimary]));
        Assert.Contains("Role=Primary", ex.Message);
    }

    [Fact]
    public void Construct_WithMultipleSideFeeds_Succeeds()
    {
        var inputs = new BacktestInputs([
            PrimaryTimeBar(),
            Side("funding-rate"),
            Side("EqV_1m_500m.flow"),
        ]);

        Assert.Equal(3, inputs.Subscriptions.Count); // primary + 2 side
        Assert.Equal(2, inputs.Subscriptions.Skip(1).Count());
    }

    [Fact]
    public void Construct_WithPrimaryRoleInSideFeeds_Throws()
    {
        var primaryRoleAsSide = new TimeBarSubscription("BTC", "ex", DataFeedRole.Primary,
            new TimeFrame(TimeSpan.FromMinutes(5)));

        var ex = Assert.Throws<ArgumentException>(() =>
            new BacktestInputs([PrimaryTimeBar(), primaryRoleAsSide]));
        Assert.Contains("Role=Side", ex.Message);
    }

    [Fact]
    public void Construct_WithNullSubscriptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BacktestInputs((IReadOnlyList<DataFeedSubscription>)null!));
    }

    [Fact]
    public void Subscriptions_PutsPrimaryFirstThenSideFeedsInOrder()
    {
        var primary = PrimaryTimeBar();
        var side1 = Side("funding-rate");
        var side2 = Side("candle-ext");
        var inputs = new BacktestInputs([primary, side1, side2]);

        var all = inputs.Subscriptions;

        Assert.Equal(3, all.Count);
        Assert.Same(primary, all[0]);
        Assert.Same(side1, all[1]);
        Assert.Same(side2, all[2]);
    }

    [Fact]
    public void Construct_FromEmptyList_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new BacktestInputs((IReadOnlyList<DataFeedSubscription>)[]));
        Assert.Contains("at least one subscription", ex.Message);
    }

    [Fact]
    public void Equals_TwoInputsWithSamePrimaryAndSideFeeds_AreEqual()
    {
        // Record value-equality is the basis for optimization-trial dedup. Pin it.
        var a = new BacktestInputs([PrimaryTimeBar(), Side("funding-rate")]);
        var b = new BacktestInputs([PrimaryTimeBar(), Side("funding-rate")]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentSideFeedOrder_AreNotEqual()
    {
        // SideFeeds order matters semantically (the engine processes them in the declared
        // order for telemetry consistency); pin that record equality preserves order.
        var a = new BacktestInputs([PrimaryTimeBar(), Side("funding-rate"), Side("candle-ext")]);
        var b = new BacktestInputs([PrimaryTimeBar(), Side("candle-ext"), Side("funding-rate")]);

        Assert.NotEqual(a, b);
    }

    // -------------------------------------------------------------------------
    // T16 — BacktestInputsFormatter.Key role-ordinal stability. The wire JSON renders
    // DataFeedRole as "Primary"/"Side" via JsonStringEnumConverter, but persisted run-key
    // hashes must use the integer ordinal so a future enum-naming change in JSON layout
    // doesn't invalidate every cached run.
    // -------------------------------------------------------------------------

    [Fact]
    public void Key_PrimaryRole_RendersOrdinalZero_NotEnumName()
    {
        var key = BacktestInputsFormatter.Key(PrimaryTimeBar());
        Assert.EndsWith(":0", key);
        Assert.DoesNotContain("Primary", key);
    }

    [Fact]
    public void Key_SideRole_RendersOrdinalOne_NotEnumName()
    {
        var key = BacktestInputsFormatter.Key(Side("funding-rate"));
        Assert.EndsWith(":1", key);
        Assert.DoesNotContain("Side", key);
    }

    [Fact]
    public void Key_TimeBarPrimary_HasFullColonDelimitedShape()
    {
        var key = BacktestInputsFormatter.Key(PrimaryTimeBar());
        // Shape: asset:exchange:feed:role-ordinal
        Assert.Equal("BTC:ex:1m:0", key);
    }

    [Fact]
    public void Key_AltBarPrimary_UsesFeedIdSegment()
    {
        var key = BacktestInputsFormatter.Key(PrimaryAltBar());
        Assert.Equal("BTC:ex:EqV_1m_500m:0", key);
    }

    [Fact]
    public void Key_TickPrimary_UsesTicksSentinel()
    {
        var key = BacktestInputsFormatter.Key(PrimaryTick());
        Assert.Equal("BTC:ex:ticks:0", key);
    }

    [Fact]
    public void Key_SideFeed_UsesFeedIdSegment()
    {
        var key = BacktestInputsFormatter.Key(Side("funding-rate"));
        Assert.Equal("BTC:ex:funding-rate:1", key);
    }
}
