using AlgoTradeForge.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Builds a <see cref="ScaleContext"/> from the asset's configured <c>DecimalDigits</c>.
/// Shared by the aggregation and catalog endpoints so per-asset threshold bounds and
/// per-job scaling agree.
/// </summary>
public static class AssetScaleContextFactory
{
    public static ScaleContext FromDecimalDigits(int decimalDigits)
    {
        var scaleFactor = (decimal)Math.Pow(10, decimalDigits);
        var tickSize = 1m / scaleFactor;
        return new ScaleContext(tickSize);
    }
}
