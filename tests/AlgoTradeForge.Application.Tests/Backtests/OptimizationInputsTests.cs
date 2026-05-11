using AlgoTradeForge.Application.Backtests;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Backtests;

/// <summary>
/// P4-11 sibling — pins <see cref="OptimizationInputs"/> ctor invariants for the
/// <c>PrimaryCandidates × ParameterGrid</c> fan-out (TRD §9.6). The shape mirrors
/// <see cref="BacktestInputs"/>: a single ordered <c>Subscriptions</c> list where
/// <c>Role</c> discriminates fan-out candidates (Primary) from shared side feeds (Side).
/// </summary>
public class OptimizationInputsTests
{
    private static TimeBarSubscription PrimaryTimeBar(string code = "1m") =>
        new("BTC", "ex", DataFeedRole.Primary, TimeFrame.Parse(code));

    private static AltBarSubscription PrimaryAltBar(string feedId = "EqV_1m_500m") =>
        new("BTC", "ex", DataFeedRole.Primary, feedId);

    private static TickSubscription PrimaryTick() =>
        new("BTC", "ex", DataFeedRole.Primary);

    private static SideFeedSubscription Side(string feedId = "funding-rate") =>
        new("BTC", "ex", DataFeedRole.Side, feedId);

    [Fact]
    public void Construct_WithSinglePrimaryAndNoSide_Succeeds()
    {
        var inputs = new OptimizationInputs([PrimaryTimeBar()]);

        Assert.Single(inputs.Subscriptions);
    }

    [Fact]
    public void Construct_WithMultiplePrimaryKinds_Succeeds()
    {
        // Fan-out across heterogeneous primary kinds is allowed; the executor switches
        // path resolution by KindOf() per primary.
        var inputs = new OptimizationInputs(
            [PrimaryTimeBar(), PrimaryAltBar(), PrimaryTick(), Side("funding-rate")]);

        Assert.Equal(4, inputs.Subscriptions.Count);
    }

    [Fact]
    public void Construct_WithEmptySubscriptions_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new OptimizationInputs([]));

        Assert.Contains("at least one subscription", ex.Message);
    }

    [Fact]
    public void Construct_WithNullSubscriptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new OptimizationInputs((IReadOnlyList<DataFeedSubscription>)null!));
    }

    [Fact]
    public void Construct_WithOnlySideFeeds_Throws()
    {
        // Without at least one Role=Primary entry, the fan-out has nothing to iterate.
        var ex = Assert.Throws<ArgumentException>(() =>
            new OptimizationInputs([Side("funding-rate"), Side("candle-ext")]));

        Assert.Contains("Role=Primary", ex.Message);
    }

    [Fact]
    public void Construct_WithSideKindMarkedPrimaryRole_Throws()
    {
        // SideFeedSubscription's ctor blocks Role=Primary (single-instance invariant), so
        // the cross-instance check we can express here would require a future Side-kind
        // subscription that allowed Role=Primary on its own — this test exists as a
        // reflexive guard for the moment a fifth subscription kind appears.
        // For now, a TimeBar with Role=Side passed as a primary candidate is structurally
        // identical to "primary slot occupied by a non-primary-eligible kind".
        var sideRoleAsPrimary = new TimeBarSubscription("BTC", "ex", DataFeedRole.Side,
            TimeFrame.Parse("1m"));

        // Constructing with only Role=Side entries fails the "no primaries" check.
        var ex = Assert.Throws<ArgumentException>(() =>
            new OptimizationInputs([sideRoleAsPrimary]));

        Assert.Contains("Role=Primary", ex.Message);
    }

    [Fact]
    public void Equals_TwoInputsWithIdenticalShape_AreEqual()
    {
        // Record value-equality is what fan-out trial dedup hangs off — pin it.
        var a = new OptimizationInputs(
            [PrimaryTimeBar(), PrimaryAltBar(), Side("funding-rate")]);
        var b = new OptimizationInputs(
            [PrimaryTimeBar(), PrimaryAltBar(), Side("funding-rate")]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentPrimaryOrder_AreNotEqual()
    {
        // Order matters: two fan-out plans that visit primaries in different orders produce
        // different per-trial sequences. Pin that record equality preserves order.
        var a = new OptimizationInputs([PrimaryTimeBar("1m"), PrimaryTimeBar("5m")]);
        var b = new OptimizationInputs([PrimaryTimeBar("5m"), PrimaryTimeBar("1m")]);

        Assert.NotEqual(a, b);
    }
}
