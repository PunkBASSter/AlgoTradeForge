using AlgoTradeForge.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Builds a <see cref="ScaleContext"/> from the asset's configured <c>DecimalDigits</c>.
/// Shared by the aggregation endpoints (POST /aggregate) and the catalog endpoints
/// (GET /aggregation-options) so per-asset threshold bounds and per-job scaling agree.
/// </summary>
/// <remarks>
/// Phase 1b uses the asset's <c>DecimalDigits</c> for tick size and lets <see cref="ScaleContext"/>
/// default the quantity scale to 1. Phase 2 will introduce per-asset <c>QuantityStepSize</c> via
/// the same factory once the config schema grows the field.
/// </remarks>
public static class AssetScaleContextFactory
{
    public static ScaleContext FromDecimalDigits(int decimalDigits)
    {
        var scaleFactor = (decimal)Math.Pow(10, decimalDigits);
        var tickSize = 1m / scaleFactor;
        return new ScaleContext(tickSize);
    }
}
