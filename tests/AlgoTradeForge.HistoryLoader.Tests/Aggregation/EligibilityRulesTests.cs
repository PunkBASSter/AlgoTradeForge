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
}
