using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// P1b-38 / P0-5 — threshold wire schema (absolute vs convenience input modes) and
/// scaling into accumulator-domain longs.
/// </summary>
public sealed class ThresholdResolverTests
{
    private static readonly ScaleContext SpotScale =
        new(tickSize: 0.01m);   // ScaleFactor=100, QuantityScale=1

    [Fact]
    public void Resolve_AbsoluteBaseAsset_RoundsToScaledLong()
    {
        var r = ThresholdResolver.Resolve(
            thresholdUnit: "base_asset",
            inputMode: "absolute",
            thresholdValue: 1000m,
            convenienceInput: null,
            scale: SpotScale);

        Assert.Equal(1000m, r.Absolute);
        Assert.Equal(1000, r.Scaled);            // QuantityScale=1
        Assert.Equal("1000", r.FeedIdComponent);
        Assert.Null(r.PreservedConvenienceInput);
    }

    [Fact]
    public void Resolve_AbsoluteQuoteAsset_AppliesScaleFactor()
    {
        var r = ThresholdResolver.Resolve(
            thresholdUnit: "quote_asset",
            inputMode: "absolute",
            thresholdValue: 1_000_000m,    // $1M USD
            convenienceInput: null,
            scale: SpotScale);

        Assert.Equal(1_000_000m, r.Absolute);
        Assert.Equal(100_000_000, r.Scaled);    // 1M × ScaleFactor=100 × QuantityScale=1
    }

    [Fact]
    public void Resolve_ConvenienceWithSiSuffix_ParsesAndPreserves()
    {
        var r = ThresholdResolver.Resolve(
            thresholdUnit: "base_asset",
            inputMode: "convenience",
            thresholdValue: null,
            convenienceInput: "1k",
            scale: SpotScale);

        Assert.Equal(1000m, r.Absolute);
        Assert.Equal(1000, r.Scaled);
        Assert.Equal("1k", r.FeedIdComponent);
        Assert.Equal("1k", r.PreservedConvenienceInput);
    }

    [Fact]
    public void Resolve_ConvenienceMillionSuffix()
    {
        var r = ThresholdResolver.Resolve(
            thresholdUnit: "quote_asset",
            inputMode: "convenience",
            thresholdValue: null,
            convenienceInput: "1M",
            scale: SpotScale);

        Assert.Equal(1_000_000m, r.Absolute);
        Assert.Equal(100_000_000, r.Scaled);
        Assert.Equal("1M", r.FeedIdComponent);
    }

    [Fact]
    public void Resolve_AbsoluteFractional_Throws()
    {
        Assert.Throws<ArgumentException>(() => ThresholdResolver.Resolve(
            thresholdUnit: "base_asset",
            inputMode: "absolute",
            thresholdValue: 0.5m,
            convenienceInput: null,
            scale: SpotScale));
    }

    [Fact]
    public void Resolve_ConvenienceMissing_Throws()
    {
        Assert.Throws<ArgumentException>(() => ThresholdResolver.Resolve(
            thresholdUnit: "base_asset",
            inputMode: "convenience",
            thresholdValue: null,
            convenienceInput: null,
            scale: SpotScale));
    }

    [Fact]
    public void Resolve_UnknownInputMode_Throws()
    {
        Assert.Throws<ArgumentException>(() => ThresholdResolver.Resolve(
            thresholdUnit: "base_asset",
            inputMode: "magic",
            thresholdValue: 100m,
            convenienceInput: null,
            scale: SpotScale));
    }

    [Fact]
    public void Resolve_UnknownThresholdUnit_Throws()
    {
        Assert.Throws<ArgumentException>(() => ThresholdResolver.Resolve(
            thresholdUnit: "ethereum_gwei",
            inputMode: "absolute",
            thresholdValue: 100m,
            convenienceInput: null,
            scale: SpotScale));
    }

    [Fact]
    public void Resolve_TradesUnit_PassesValueDirectly()
    {
        var r = ThresholdResolver.Resolve(
            thresholdUnit: "trades",
            inputMode: "absolute",
            thresholdValue: 500m,
            convenienceInput: null,
            scale: SpotScale);

        Assert.Equal(500, r.Scaled);
    }

    [Fact]
    public void Resolve_PriceUnit_AbsoluteScalesByTickSizeOnly()
    {
        // P5-0 — Range/Renko threshold is a price magnitude (e.g. $50 per bar).
        // No QuantityScale factor — distinct from base_asset/quote_asset.
        var r = ThresholdResolver.Resolve(
            thresholdUnit: "price",
            inputMode: "absolute",
            thresholdValue: 50m,
            convenienceInput: null,
            scale: SpotScale);

        Assert.Equal(50m, r.Absolute);
        Assert.Equal(5000, r.Scaled);            // 50 × ScaleFactor=100 (no QuantityScale factor)
        Assert.Equal("50", r.FeedIdComponent);
        Assert.Null(r.PreservedConvenienceInput);
    }

    [Fact]
    public void Resolve_PriceUnit_ConvenienceSiSuffix_RoundTrips()
    {
        // "1k" price = $1000 range threshold.
        var r = ThresholdResolver.Resolve(
            thresholdUnit: "price",
            inputMode: "convenience",
            thresholdValue: null,
            convenienceInput: "1k",
            scale: SpotScale);

        Assert.Equal(1000m, r.Absolute);
        Assert.Equal(100_000, r.Scaled);         // 1000 × 100
        Assert.Equal("1k", r.FeedIdComponent);
        Assert.Equal("1k", r.PreservedConvenienceInput);
    }

    [Fact]
    public void Resolve_PriceUnit_DoesNotMultiplyByQuantityScale()
    {
        // Regression guard: scaling MUST mirror price scaling, not base_asset / quote_asset.
        // Use a non-unit QuantityScale so a bug ("price" branch falling through to
        // base_asset's MoneyConvert.ToLong(absolute * QuantityScale)) would surface.
        var perpScale = new ScaleContext(tickSize: 0.1m, quantityStepSize: 0.001m);   // ScaleFactor=10, QuantityScale=1000
        var r = ThresholdResolver.Resolve(
            thresholdUnit: "price",
            inputMode: "absolute",
            thresholdValue: 50m,
            convenienceInput: null,
            scale: perpScale);

        Assert.Equal(50m, r.Absolute);
        Assert.Equal(500, r.Scaled);             // 50 × ScaleFactor=10 ONLY (NOT × 1000)
    }

    [Fact]
    public void Resolve_BothInputModesRoundTripToSameAbsolute()
    {
        // P1b-38 — request `absolute` (1000) and `convenience` (`1k`) produce the same
        // canonical absolute value. Their FeedIdComponent differs (per grammar) but the
        // accumulator threshold is identical.
        var byAbs = ThresholdResolver.Resolve("base_asset", "absolute", 1000m, null, SpotScale);
        var byCon = ThresholdResolver.Resolve("base_asset", "convenience", null, "1k", SpotScale);

        Assert.Equal(byAbs.Absolute, byCon.Absolute);
        Assert.Equal(byAbs.Scaled, byCon.Scaled);
        Assert.NotEqual(byAbs.FeedIdComponent, byCon.FeedIdComponent);   // "1000" vs "1k"
    }

    // -------------------------------------------------------------------------
    // Q-3 — per-unit, per-asset minimum-threshold floor
    // -------------------------------------------------------------------------

    [Fact]
    public void MinimumAbsolute_BaseAsset_DependsOnQuantityScale()
    {
        // QuantityScale = 1/qStep. A qStep of 0.0001 means the smallest base_asset value that
        // scales to >=1 is 0.0001 (since 0.0001 * 10000 = 1).
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        Assert.Equal(0.0001m, ThresholdResolver.MinimumAbsolute("base_asset", scale));
    }

    [Fact]
    public void MinimumAbsolute_QuoteAsset_DependsOnTickSizeAndQuantityScale()
    {
        // tickSize=0.1, qStep=0.001 → ScaleFactor=10, QuantityScale=1000.
        // Smallest absolute that scales to >=1 in (absolute * QuantityScale * ScaleFactor) is
        //   1 / (1000 * 10) = 0.0001 → equivalently TickSize / QuantityScale = 0.1 / 1000 = 0.0001.
        var scale = new ScaleContext(tickSize: 0.1m, quantityStepSize: 0.001m);
        Assert.Equal(0.0001m, ThresholdResolver.MinimumAbsolute("quote_asset", scale));
    }

    [Fact]
    public void MinimumAbsolute_Trades_AlwaysOne()
    {
        Assert.Equal(1m, ThresholdResolver.MinimumAbsolute("trades", new ScaleContext(0.01m)));
        Assert.Equal(1m, ThresholdResolver.MinimumAbsolute("trades", new ScaleContext(1m, 1m)));
        Assert.Equal(1m, ThresholdResolver.MinimumAbsolute("trades", new ScaleContext(0.0001m, 0.00001m)));
    }

    [Fact]
    public void MinimumAbsolute_Price_EqualsTickSize()
    {
        Assert.Equal(0.01m, ThresholdResolver.MinimumAbsolute("price", new ScaleContext(0.01m)));
        Assert.Equal(0.5m, ThresholdResolver.MinimumAbsolute("price", new ScaleContext(0.5m)));
        // QuantityScale must not affect price floor.
        Assert.Equal(0.1m, ThresholdResolver.MinimumAbsolute("price",
            new ScaleContext(tickSize: 0.1m, quantityStepSize: 0.001m)));
    }

    [Fact]
    public void Resolve_BelowFloor_ThrowsActionable()
    {
        // tickSize=0.01, qStep=0.0001 → QuantityScale=10000 → base_asset floor = 1/10000 = 0.0001.
        // Convenience "1u" = 0.000001 is two orders of magnitude below the floor → throws.
        var perpScale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var ex = Assert.Throws<ArgumentException>(() =>
            ThresholdResolver.Resolve("base_asset", "convenience", null, "1u", perpScale));
        Assert.Contains("minimum", ex.Message);
        Assert.Contains("base_asset", ex.Message);
        Assert.Contains("0.0001", ex.Message);                   // floor value
        Assert.Contains("convenience input", ex.Message);         // hint
    }

    [Fact]
    public void Resolve_AtFloor_Succeeds_ScaledEqualsOne()
    {
        // base_asset with QuantityScale=1 → floor=1m → scaled=1L.
        var r = ThresholdResolver.Resolve("base_asset", "absolute", 1m, null, SpotScale);
        Assert.Equal(1L, r.Scaled);

        // price with tickSize=0.01 → floor=0.01 → scaled=1L. Use convenience because absolute
        // mode requires integral input (existing P1b constraint).
        var rPrice = ThresholdResolver.Resolve("price", "convenience", null, "10m", SpotScale);   // 10m = 0.01
        Assert.Equal(1L, rPrice.Scaled);
    }

    [Fact]
    public void Resolve_BelowFloor_QuotesUserInputVerbatim()
    {
        // The resolved absolute value should appear in the message in its decimal form,
        // not as a rounded long. Convenience input "5u" resolves to 0.000005 base which
        // is below the 0.0001 floor on this scale — message must contain BOTH the resolved
        // 0.000005 (so the user can compare against the floor) AND the original "5u" string
        // (so the user can replay/correct what they actually typed).
        var perpScale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var ex = Assert.Throws<ArgumentException>(() =>
            ThresholdResolver.Resolve("base_asset", "convenience", null, "5u", perpScale));   // 5u = 0.000005
        Assert.Contains("0.000005", ex.Message);
        Assert.Contains("5u", ex.Message);
    }

    [Fact]
    public void Resolve_BelowFloor_AbsoluteMode_OmitsConvenienceInput()
    {
        // When the user submitted absolute mode, there is no convenience-input string to echo —
        // the message must NOT wrap the input value in a `(<convenience>)` parenthetical
        // (the hint trailer "(use convenience input '...' or larger)" is unrelated and DOES
        // appear). Construct a scale where the floor exceeds 1 so absolute=1 fires the floor
        // check (price floor = TickSize = 10).
        var coarseScale = new ScaleContext(tickSize: 10m);
        var ex = Assert.Throws<ArgumentException>(() =>
            ThresholdResolver.Resolve("price", "absolute", 1m, null, coarseScale));
        Assert.Contains("1 price", ex.Message);
        // The convenience-echo branch would render as "1 (<input>) price" — make sure no such
        // parenthetical appears between the value and the unit.
        Assert.DoesNotContain("1 (", ex.Message);
    }

    [Fact]
    public void Resolve_BelowFloor_HintPicksSmallestMantissa()
    {
        // Fix 4 (SuggestSiSuffix reorder). On a scale where base_asset floor = 0.001, the hint
        // must read "1m" — the smallest mantissa form — rather than the equivalent "1000u".
        // qStep = 0.001 → QuantityScale = 1000 → base_asset floor = 1 / 1000 = 0.001.
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.001m);
        var ex = Assert.Throws<ArgumentException>(() =>
            ThresholdResolver.Resolve("base_asset", "convenience", null, "1u", scale));
        Assert.Contains("'1m'", ex.Message);
        Assert.DoesNotContain("'1000u'", ex.Message);
    }

    [Fact]
    public void MinimumAbsolute_UnknownUnit_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ThresholdResolver.MinimumAbsolute("bogus", SpotScale));
    }

    [Fact]
    public void MinimumAbsolute_PerpScale_DiffersByUnit()
    {
        // On a perp scale (qStep != 1), the per-unit minima are not all equal — Resolve()
        // enforces the per-unit floor at request time, which is the actual Q-3 guarantee.
        // (CatalogEndpoints used to return Max-of-floors but reduced to a literal 1m once we
        // observed it always collapses to "trades" on typical crypto scales.)
        var perpScale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var baseFloor  = ThresholdResolver.MinimumAbsolute("base_asset",  perpScale);   // 0.0001
        var quoteFloor = ThresholdResolver.MinimumAbsolute("quote_asset", perpScale);   // 0.000001
        var tradesFloor = ThresholdResolver.MinimumAbsolute("trades",     perpScale);   // 1
        var priceFloor = ThresholdResolver.MinimumAbsolute("price",       perpScale);   // 0.01

        Assert.NotEqual(baseFloor, quoteFloor);
        Assert.NotEqual(baseFloor, priceFloor);
        Assert.Equal(1m, tradesFloor);
        Assert.Equal(0.01m, priceFloor);
    }

    // -------------------------------------------------------------------------
    // GetImplicitUnit — Fix 2 in PR-31 review. Pins the type→unit mapping so endpoint
    // validation and the AltBar-source ordering check stay apples-to-apples.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("EqV", "base_asset")]
    [InlineData("EqT", "trades")]
    [InlineData("EqD", "quote_asset")]
    [InlineData("EqI", "base_asset")]
    [InlineData("EqID", "quote_asset")]
    [InlineData("EqIT", "trades")]
    [InlineData("Range", "price")]
    [InlineData("Renko", "price")]
    public void GetImplicitUnit_ReturnsExpectedForAllTypes(string typeCode, string expectedUnit)
    {
        Assert.Equal(expectedUnit, ThresholdResolver.GetImplicitUnit(typeCode));
    }

    [Fact]
    public void GetImplicitUnit_UnknownType_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ThresholdResolver.GetImplicitUnit("Bogus"));
        Assert.Contains("type_code", ex.Message);
        Assert.Contains("Bogus", ex.Message);
    }

    [Fact]
    public void GetImplicitUnit_NullOrEmpty_Throws()
    {
        // ThrowIfNullOrEmpty raises ArgumentException for "", ArgumentNullException for null —
        // both inherit ArgumentException, so ThrowsAny covers the contract without pinning
        // which BCL helper the impl uses.
        Assert.ThrowsAny<ArgumentException>(() => ThresholdResolver.GetImplicitUnit(""));
        Assert.ThrowsAny<ArgumentException>(() => ThresholdResolver.GetImplicitUnit(null!));
    }

    [Fact]
    public void GetImplicitUnit_EveryAllowedTypeMapsToValidResolverUnit()
    {
        // Round-trip: every implicit unit must round-trip through MinimumAbsolute and Resolve
        // without throwing "unrecognized threshold_unit". Catches drift between the type-code
        // table and ThresholdResolver's switch.
        foreach (var typeCode in new[] { "EqV", "EqT", "EqD", "EqI", "EqID", "EqIT", "Range", "Renko" })
        {
            var unit = ThresholdResolver.GetImplicitUnit(typeCode);
            // Must not throw — this is the pin.
            _ = ThresholdResolver.MinimumAbsolute(unit, SpotScale);
        }
    }
}
