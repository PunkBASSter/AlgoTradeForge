using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>P1b-26 / TRD §7 — source/type compatibility matrix.</summary>
public sealed class EligibilityRulesTests
{
    private static FeedDefinition TimeBar(params string[] columns) =>
        new() { Kind = "OHLCV_TimeBar", Interval = "1m", Columns = columns };

    [Fact]
    public void Tick_AllTypesEligible()
    {
        var def = new FeedDefinition { Kind = "Tick", Columns = ["ts", "price", "qty", "is_buyer_maker"] };
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: false);

        Assert.Equal(["EqT", "EqV", "EqD", "EqI", "Range", "Renko"], r.EligibleTypes.ToArray());
        Assert.Empty(r.IneligibleTypes);
    }

    [Fact]
    public void TimeBarPerpWithCandleExt_AllVolumeTypesPlusEqI_WithWarning()
    {
        var def = TimeBar("ts", "o", "h", "l", "c", "vol");
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: true);

        Assert.Equal(["EqT", "EqV", "EqD", "EqI"], r.EligibleTypes.ToArray());
        Assert.Single(r.Warnings);   // m1_taker_buy_proxy fidelity warning
    }

    [Fact]
    public void TimeBarSpotWithCandleExt_EqIIneligible()
    {
        var def = TimeBar("ts", "o", "h", "l", "c", "vol");
        var r = EligibilityRules.ForSource(def, "spot", hasCandleExt: true);

        Assert.Equal(["EqT", "EqV", "EqD"], r.EligibleTypes.ToArray());
        Assert.Single(r.IneligibleTypes, e => e.Code == "EqI");
    }

    [Fact]
    public void TimeBarWithoutCandleExt_EqIIneligible()
    {
        var def = TimeBar("ts", "o", "h", "l", "c", "vol");
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: false);

        Assert.Equal(["EqT", "EqV", "EqD"], r.EligibleTypes.ToArray());
        Assert.Single(r.IneligibleTypes, e => e.Code == "EqI");
    }

    [Fact]
    public void AltBarSource_NoEligibleTypes()
    {
        var def = new FeedDefinition { Kind = "OHLCV_AltBar", Columns = ["ts", "o", "h", "l", "c", "vol"] };
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: true);

        Assert.Empty(r.EligibleTypes);
        // All six alt-bar codes ineligible; reasons all reference re-aggregation.
        Assert.Equal(6, r.IneligibleTypes.Count);
    }

    [Fact]
    public void SideFeed_NoEligibleTypes()
    {
        var def = new FeedDefinition { Kind = "Side", Columns = ["ts", "rate"] };
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: true);

        Assert.Empty(r.EligibleTypes);
        Assert.Equal(6, r.IneligibleTypes.Count);
    }

    // P5-11 — Range/Renko narrowing (ADR P5-1 D1: tick-only in v1).

    private const string RangeRenkoTickReason = "Range/Renko require a tick source for fidelity in v1.";

    [Fact]
    public void TimeBarPerpWithCandleExt_RangeRenkoIneligible_WithCanonicalReason()
    {
        var def = TimeBar("ts", "o", "h", "l", "c", "vol");
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: true);

        Assert.DoesNotContain("Range", r.EligibleTypes);
        Assert.DoesNotContain("Renko", r.EligibleTypes);
        Assert.Single(r.IneligibleTypes, e => e.Code == "Range" && e.Reason == RangeRenkoTickReason);
        Assert.Single(r.IneligibleTypes, e => e.Code == "Renko" && e.Reason == RangeRenkoTickReason);
    }

    [Fact]
    public void TimeBarSpotWithCandleExt_RangeRenkoIneligible()
    {
        var def = TimeBar("ts", "o", "h", "l", "c", "vol");
        var r = EligibilityRules.ForSource(def, "spot", hasCandleExt: true);

        Assert.Single(r.IneligibleTypes, e => e.Code == "Range" && e.Reason == RangeRenkoTickReason);
        Assert.Single(r.IneligibleTypes, e => e.Code == "Renko" && e.Reason == RangeRenkoTickReason);
    }

    [Fact]
    public void TimeBarWithoutCandleExt_RangeRenkoIneligible()
    {
        var def = TimeBar("ts", "o", "h", "l", "c", "vol");
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: false);

        Assert.Single(r.IneligibleTypes, e => e.Code == "Range" && e.Reason == RangeRenkoTickReason);
        Assert.Single(r.IneligibleTypes, e => e.Code == "Renko" && e.Reason == RangeRenkoTickReason);
    }

    [Fact]
    public void OhlcOnlyTimeBar_RangeRenkoAlsoIneligible()
    {
        // OHLC-only sources lose volume types AND Range/Renko (per Phase 5 v1 scope).
        var def = TimeBar("ts", "o", "h", "l", "c");     // no vol column
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: false);

        Assert.Empty(r.EligibleTypes);
        Assert.Single(r.IneligibleTypes, e => e.Code == "Range" && e.Reason == RangeRenkoTickReason);
        Assert.Single(r.IneligibleTypes, e => e.Code == "Renko" && e.Reason == RangeRenkoTickReason);
    }

    [Fact]
    public void Tick_RangeRenkoEligible_NoIneligibleEntries()
    {
        // Tick is the only source where Range/Renko make the EligibleTypes list.
        var def = new FeedDefinition { Kind = "Tick", Columns = ["ts", "price", "qty", "is_buyer_maker"] };
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: false);

        Assert.Contains("Range", r.EligibleTypes);
        Assert.Contains("Renko", r.EligibleTypes);
        Assert.DoesNotContain(r.IneligibleTypes, e => e.Code == "Range");
        Assert.DoesNotContain(r.IneligibleTypes, e => e.Code == "Renko");
    }

    [Fact]
    public void AltBarSource_RangeRenkoIneligible_WithReaggregationReason()
    {
        // Re-aggregating from an alt-bar source surfaces the broader v1 restriction (Phase 6),
        // not the tick-only reason — every alt-bar type code is blocked, not just Range/Renko.
        var def = new FeedDefinition { Kind = "OHLCV_AltBar", Columns = ["ts", "o", "h", "l", "c", "vol"] };
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: false);

        Assert.Empty(r.EligibleTypes);
        var rangeEntry = Assert.Single(r.IneligibleTypes, e => e.Code == "Range");
        Assert.Contains("Re-aggregation from alt-bar sources", rangeEntry.Reason);
        var renkoEntry = Assert.Single(r.IneligibleTypes, e => e.Code == "Renko");
        Assert.Contains("Re-aggregation from alt-bar sources", renkoEntry.Reason);
    }

    [Fact]
    public void SideSource_RangeRenkoIneligible_WithCannotBeAggregatedReason()
    {
        // Side feeds (taker-buy / liquidation streams) are non-aggregable across the board.
        var def = new FeedDefinition { Kind = "Side", Columns = ["ts", "value"] };
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: false);

        Assert.Empty(r.EligibleTypes);
        var rangeEntry = Assert.Single(r.IneligibleTypes, e => e.Code == "Range");
        Assert.Contains("Side feeds cannot be aggregated", rangeEntry.Reason);
        var renkoEntry = Assert.Single(r.IneligibleTypes, e => e.Code == "Renko");
        Assert.Contains("Side feeds cannot be aggregated", renkoEntry.Reason);
    }
}
