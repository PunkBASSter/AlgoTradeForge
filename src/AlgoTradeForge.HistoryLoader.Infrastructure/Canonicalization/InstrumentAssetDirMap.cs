using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

/// <summary>
/// Resolves a relay instrument string (e.g. "BTCUSDT") to the canonical asset directory the
/// CSV writers partition under. Resolution is plan-based: dir = asset.Venue.Dir, matched by
/// Venue.ApiSymbol (OrdinalIgnoreCase) AND venue class (futures vs spot) to disambiguate
/// BTCUSDT spot from BTCUSDT_perp. Absent instruments fall back to {baseDir}/{venue}/{instrument}.
/// </summary>
public sealed class InstrumentAssetDirMap(string baseDir, ICollectionPlanSource planSource)
{
    // "binance-futures", "fapi", "perp" → futures; "binance", "spot", etc. → spot/other.
    private static bool IsFuturesVenue(string venue) =>
        venue.Contains("futures", StringComparison.OrdinalIgnoreCase)
        || venue.Contains("perp", StringComparison.OrdinalIgnoreCase)
        || venue.Contains("fapi", StringComparison.OrdinalIgnoreCase);

    public string Resolve(string venue, string instrument)
    {
        var futuresVenue = IsFuturesVenue(venue);
        var asset = planSource.Current.Assets.FirstOrDefault(a =>
            string.Equals(a.Venue.ApiSymbol, instrument, StringComparison.OrdinalIgnoreCase)
            && AssetTypes.IsFutures(a.Venue.AssetType) == futuresVenue);
        return asset is not null
            ? Path.Combine(baseDir, asset.Exchange, asset.Venue.Dir)
            : Path.Combine(baseDir, venue, instrument);
    }

    public string VenueDir(string venue) => Path.Combine(baseDir, venue);

    /// <summary>Per-instrument tick scale (DecimalDigits) from the plan; null when unconfigured.</summary>
    public int? ResolveDigits(string venue, string instrument)
    {
        var futuresVenue = IsFuturesVenue(venue);
        var asset = planSource.Current.Assets.FirstOrDefault(a =>
            string.Equals(a.Venue.ApiSymbol, instrument, StringComparison.OrdinalIgnoreCase)
            && AssetTypes.IsFutures(a.Venue.AssetType) == futuresVenue);
        return asset?.DecimalDigits;
    }
}
