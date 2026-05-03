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
}
