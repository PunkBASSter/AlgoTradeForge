using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.HistoryLoader.Domain;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Subscriptions;

/// <summary>
/// P4-8 — Pins that <see cref="AltBarSubscription.FeedId"/> values produced by the
/// system round-trip through the §3.3 positional grammar (validated via
/// <see cref="AltBarFeedId.TryParse"/>) and that record-equality respects per-component
/// distinctness, including the X-5 ambiguity case (<c>EqV_1m_500m</c> vs
/// <c>EqV_5m_500m</c>).
/// </summary>
/// <remarks>
/// The Domain project doesn't reference HistoryLoader.Domain (layering — history is a
/// peripheral concern). The test project DOES reference it so we can assert grammar
/// conformance here without polluting the production graph.
/// </remarks>
public class AltBarSubscriptionTests
{
    [Theory]
    [InlineData("EqV_1m_500m")]
    [InlineData("EqV_5m_500m")]
    [InlineData("EqT_1h_500")]
    [InlineData("EqD_1m_1k")]
    [InlineData("EqIV_ticks_1M")]
    public void FeedId_ConformsToSection33Grammar(string feedId)
    {
        var sub = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, feedId);

        Assert.True(
            AltBarFeedId.TryParse(sub.FeedId, out var parsed, out var error),
            $"FeedId '{feedId}' should parse cleanly; error: {error}");
        Assert.NotNull(parsed);
        Assert.Equal(feedId, parsed!.FeedId);
    }

    [Fact]
    public void X5_Ambiguity_OneMinuteVsFiveMinute_ProducesDistinctRecords()
    {
        // The §3.3 grammar is positional: component-2 is the source-code, component-3 is
        // the threshold. EqV_1m_500m and EqV_5m_500m differ only in source-code; the
        // parser MUST keep them distinct, and so must record-equality on AltBarSubscription.
        var oneMinute = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_500m");
        var fiveMinute = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_5m_500m");

        Assert.NotEqual(oneMinute, fiveMinute);

        Assert.True(AltBarFeedId.TryParse(oneMinute.FeedId, out var p1, out _));
        Assert.True(AltBarFeedId.TryParse(fiveMinute.FeedId, out var p5, out _));
        Assert.Equal("1m", p1!.SourceCode);
        Assert.Equal("5m", p5!.SourceCode);
        Assert.Equal(p1.Threshold, p5.Threshold);
    }

    [Fact]
    public void DuplicateInList_DetectableViaRecordEquality()
    {
        // Collision-detection during BacktestInputs validation will rely on record-
        // equality to dedup repeats in a fan-out group. Pin that AltBarSubscriptions with
        // identical (assetName, exchange, role, feedId) compare equal.
        var a = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_500m");
        var b = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_500m");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var distinct = new HashSet<AltBarSubscription> { a, b };
        Assert.Single(distinct);
    }

    [Fact]
    public void DistinctRoles_AreNotEqual()
    {
        // A primary candidate set for fan-out should permit the same FeedId to appear
        // as both Primary (in candidates) and Side (in side feeds) without triggering
        // dedup — record-equality must factor Role into the comparison.
        var primary = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_500m");
        var side = new AltBarSubscription("BTC", "ex", DataFeedRole.Side, "EqV_1m_500m");

        Assert.NotEqual(primary, side);
    }

    [Fact]
    public void DistinctFeedIds_AreNotEqual_AndCoexistInHashSet()
    {
        // Locks the dedup contract for fan-out groups: two AltBarSubscriptions that differ
        // ONLY by FeedId (same asset+exchange+role) are unequal and both survive a HashSet.
        // Pins `FeedId` as part of the auto-generated record equality. A future refactor that
        // moves FeedId out of the primary constructor (e.g. into a non-record property after
        // the closing `;`) would silently merge `EqV_1m_500m` and `EqV_1m_1k` — this test
        // surfaces that as a failure.
        var a = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_500m");
        var b = new AltBarSubscription("BTC", "ex", DataFeedRole.Primary, "EqV_1m_1k");

        Assert.NotEqual(a, b);

        var set = new HashSet<AltBarSubscription> { a, b };
        Assert.Equal(2, set.Count);
    }
}
