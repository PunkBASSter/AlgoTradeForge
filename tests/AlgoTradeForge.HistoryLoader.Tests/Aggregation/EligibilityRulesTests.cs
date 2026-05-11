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

        Assert.Equal(["EqT", "EqV", "EqD", "EqIV", "EqID", "EqIT", "Range", "Renko"], r.EligibleTypes.ToArray());
        Assert.Empty(r.IneligibleTypes);
    }

    [Fact]
    public void TimeBarPerpWithCandleExt_AllVolumeTypesPlusImbalance_WithWarnings()
    {
        var def = TimeBar("ts", "o", "h", "l", "c", "vol");
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: true);

        // EqIV / EqID / EqIT all eligible on perp time-bar with candle-ext (each has its own
        // proxy fidelity tag); Range/Renko remain tick-only.
        Assert.Equal(["EqT", "EqV", "EqD", "EqIV", "EqID", "EqIT"], r.EligibleTypes.ToArray());
        Assert.Equal(3, r.Warnings.Count);   // EqIV proxy + EqID proxy + EqIT approximation
        Assert.Contains(AltBarWarnings.TimeBarEqIProxy, r.Warnings);
        Assert.Contains(AltBarWarnings.TimeBarEqIDProxy, r.Warnings);
        Assert.Contains(AltBarWarnings.TimeBarTibApproximation, r.Warnings);
    }

    [Fact]
    public void TimeBarSpotWithCandleExt_AllImbalanceIneligible()
    {
        var def = TimeBar("ts", "o", "h", "l", "c", "vol");
        var r = EligibilityRules.ForSource(def, "spot", hasCandleExt: true);

        // Spot rejects every imbalance variant (perp-only proxies); EqV/EqT/EqD still work.
        Assert.Equal(["EqT", "EqV", "EqD"], r.EligibleTypes.ToArray());
        Assert.Single(r.IneligibleTypes, e => e.Code == "EqIV");
        Assert.Single(r.IneligibleTypes, e => e.Code == "EqID");
        Assert.Single(r.IneligibleTypes, e => e.Code == "EqIT");
    }

    [Fact]
    public void TimeBarWithoutCandleExt_AllImbalanceIneligible()
    {
        var def = TimeBar("ts", "o", "h", "l", "c", "vol");
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: false);

        Assert.Equal(["EqT", "EqV", "EqD"], r.EligibleTypes.ToArray());
        Assert.Single(r.IneligibleTypes, e => e.Code == "EqIV");
        Assert.Single(r.IneligibleTypes, e => e.Code == "EqID");
        Assert.Single(r.IneligibleTypes, e => e.Code == "EqIT");
    }

    [Fact]
    public void AltBarSource_NoEligibleTypes()
    {
        var def = new FeedDefinition { Kind = "OHLCV_AltBar", Columns = ["ts", "o", "h", "l", "c", "vol"] };
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: true);

        Assert.Empty(r.EligibleTypes);
        // All eight alt-bar codes ineligible; reasons all reference re-aggregation.
        Assert.Equal(8, r.IneligibleTypes.Count);
    }

    [Fact]
    public void SideFeed_NoEligibleTypes()
    {
        var def = new FeedDefinition { Kind = "Side", Columns = ["ts", "rate"] };
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: true);

        Assert.Empty(r.EligibleTypes);
        Assert.Equal(8, r.IneligibleTypes.Count);
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

    // P6-12 — re-aggregation eligibility (Phase 6).

    private static FeedDefinition AltBarDef(string typeCode) => new()
    {
        Kind = "OHLCV_AltBar",
        Columns = ["ts", "o", "h", "l", "c", "vol"],
        Type = new AggregatedTypeInfo { Code = typeCode, Name = typeCode },
    };

    [Fact]
    public void AltBarSource_EqV_AllowsLargerEqV_RejectsOthers()
    {
        // EqV source is safe-trio: only EqV can be re-aggregated from it (cross-family rejected).
        var r = EligibilityRules.ForSource(AltBarDef("EqV"), "perpetual", hasCandleExt: false);

        Assert.Single(r.EligibleTypes, t => t == "EqV");
        Assert.Equal(7, r.IneligibleTypes.Count);
        var eqtEntry = Assert.Single(r.IneligibleTypes, e => e.Code == "EqT");
        Assert.Contains("same type family", eqtEntry.Reason);
    }

    [Fact]
    public void AltBarSource_EqT_AllowsLargerEqT_RejectsOthers()
    {
        var r = EligibilityRules.ForSource(AltBarDef("EqT"), "perpetual", hasCandleExt: false);

        Assert.Single(r.EligibleTypes, t => t == "EqT");
        Assert.DoesNotContain(r.IneligibleTypes, e => e.Code == "EqT");
    }

    [Fact]
    public void AltBarSource_EqD_AllowsLargerEqD_RejectsOthers()
    {
        var r = EligibilityRules.ForSource(AltBarDef("EqD"), "perpetual", hasCandleExt: false);

        Assert.Single(r.EligibleTypes, t => t == "EqD");
        Assert.DoesNotContain(r.IneligibleTypes, e => e.Code == "EqD");
    }

    [Fact]
    public void AltBarSource_EqIV_RejectsAll_WithFidelityReason()
    {
        // EqIV source: signed-imbalance trajectory is collapsed; no re-aggregation in v1.
        var r = EligibilityRules.ForSource(AltBarDef("EqIV"), "perpetual", hasCandleExt: false);

        Assert.Empty(r.EligibleTypes);
        Assert.Equal(8, r.IneligibleTypes.Count);
        Assert.All(r.IneligibleTypes, e => Assert.Contains("EqIV re-aggregation deferred", e.Reason));
    }

    [Fact]
    public void AltBarSource_EqID_RejectsAll_WithImbalanceReason()
    {
        // Same fidelity argument as EqIV: dollar-imbalance trajectory is also collapsed.
        var r = EligibilityRules.ForSource(AltBarDef("EqID"), "perpetual", hasCandleExt: false);

        Assert.Empty(r.EligibleTypes);
        Assert.Equal(8, r.IneligibleTypes.Count);
        Assert.All(r.IneligibleTypes, e => Assert.Contains("Imbalance re-aggregation deferred", e.Reason));
    }

    [Fact]
    public void AltBarSource_EqIT_RejectsAll_WithImbalanceReason()
    {
        // Tick-count imbalance: same trajectory-loss argument.
        var r = EligibilityRules.ForSource(AltBarDef("EqIT"), "perpetual", hasCandleExt: false);

        Assert.Empty(r.EligibleTypes);
        Assert.Equal(8, r.IneligibleTypes.Count);
        Assert.All(r.IneligibleTypes, e => Assert.Contains("Imbalance re-aggregation deferred", e.Reason));
    }

    [Fact]
    public void AltBarSource_Range_RejectsAll_WithPathDependentReason()
    {
        var r = EligibilityRules.ForSource(AltBarDef("Range"), "perpetual", hasCandleExt: false);

        Assert.Empty(r.EligibleTypes);
        Assert.All(r.IneligibleTypes, e => Assert.Contains("path-dependent", e.Reason));
    }

    [Fact]
    public void AltBarSource_Renko_RejectsAll_WithPathDependentReason()
    {
        var r = EligibilityRules.ForSource(AltBarDef("Renko"), "perpetual", hasCandleExt: false);

        Assert.Empty(r.EligibleTypes);
        Assert.All(r.IneligibleTypes, e => Assert.Contains("path-dependent", e.Reason));
    }

    [Fact]
    public void AltBarSource_MissingTypeMetadata_RejectsAllWithDiagnosticReason()
    {
        // Defense-in-depth: an alt-bar entry without populated Type field can't be re-aggregated.
        var def = new FeedDefinition { Kind = "OHLCV_AltBar", Columns = ["ts", "o", "h", "l", "c", "vol"] };
        var r = EligibilityRules.ForSource(def, "perpetual", hasCandleExt: false);

        Assert.Empty(r.EligibleTypes);
        Assert.All(r.IneligibleTypes, e => Assert.Contains("missing type metadata", e.Reason));
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
