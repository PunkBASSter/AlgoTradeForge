using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Aggregation;

/// <summary>
/// Freezes the alt-bar live-threshold derivation (M6 parity): a parsed feed-id threshold
/// resolves to the same scaled long the batch pipeline persisted, via the typeCode's implicit unit.
/// </summary>
public sealed class ThresholdResolverResolveParsedTests
{
    private static readonly ScaleContext SpotScale = new(tickSize: 0.01m); // ScaleFactor=100, QuantityScale=1

    // QuantityScale=1000 (3 quantity decimals); a sub-unit base threshold like 0.5 is only valid
    // when the asset's quantity step admits it — mirrors a real crypto asset's ScaleContext(asset).
    private static readonly ScaleContext FractionalQtyScale = new(tickSize: 0.01m, quantityStepSize: 0.001m);

    [Fact]
    public void ResolveParsed_EqV_BaseAsset_SubUnitThreshold()
    {
        // EqV → base_asset; 500m = 0.5 base; 0.5 × QuantityScale(1000) = 500.
        var scaled = ThresholdResolver.ResolveParsed("EqV", new ThresholdValue(500L, 'm'), FractionalQtyScale);
        Assert.Equal(500L, scaled);
    }

    [Fact]
    public void ResolveParsed_EqV_BaseAsset_IntegerThreshold()
    {
        var scaled = ThresholdResolver.ResolveParsed("EqV", new ThresholdValue(1000L, ThresholdValue.NoSuffix), SpotScale);
        Assert.Equal(1000L, scaled);
    }

    [Fact]
    public void ResolveParsed_EqD_QuoteAsset_AppliesScaleFactor()
    {
        // EqD → quote_asset; 2M = $2,000,000; × ScaleFactor(100) × QuantityScale(1).
        var scaled = ThresholdResolver.ResolveParsed("EqD", new ThresholdValue(2L, 'M'), SpotScale);
        Assert.Equal(200_000_000L, scaled);
    }

    [Fact]
    public void ResolveParsed_MatchesResolveViaImplicitUnit()
    {
        var viaConvenience = ThresholdResolver.Resolve(
            ThresholdResolver.GetImplicitUnit("EqD"),
            inputMode: "convenience",
            thresholdValue: null,
            convenienceInput: "2M",
            scale: SpotScale).Scaled;

        var viaParsed = ThresholdResolver.ResolveParsed("EqD", new ThresholdValue(2L, 'M'), SpotScale);

        Assert.Equal(viaConvenience, viaParsed);
    }
}
