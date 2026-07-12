using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

/// <summary>
/// Resolves a relay instrument string (e.g. "BTCUSDT") to the canonical asset directory the
/// CSV writers partition under. Resolution is plan-based: dir = asset.Venue.Dir, matched by
/// Venue.ApiSymbol (OrdinalIgnoreCase) AND venue class (futures vs spot) to disambiguate
/// BTCUSDT spot from BTCUSDT_perp. Absent instruments fall back to {baseDir}/{venue}/{instrument}.
/// Invariant: dir/digits resolution is stable within a canonicalization session — the plan is
/// snapshotted at construction and refreshed only by <see cref="BeginSession"/> (called from
/// projection Seed, strictly before any Apply). A Publish mid-session cannot split one stream's
/// frames across two asset dirs; plan changes take effect on the next session.
/// </summary>
public sealed class InstrumentAssetDirMap(string baseDir, ICollectionPlanSource planSource)
{
    private CollectionPlan _session = planSource.Current;

    public void BeginSession() => _session = planSource.Current;

    // "binance-futures", "fapi", "perp" → futures; "binance", "spot", etc. → spot/other.
    private static bool IsFuturesVenue(string venue) =>
        venue.Contains("futures", StringComparison.OrdinalIgnoreCase)
        || venue.Contains("perp", StringComparison.OrdinalIgnoreCase)
        || venue.Contains("fapi", StringComparison.OrdinalIgnoreCase);

    private CollectionAsset? Find(string venue, string instrument)
    {
        var futuresVenue = IsFuturesVenue(venue);
        return _session.Assets.FirstOrDefault(a =>
            string.Equals(a.Venue.ApiSymbol, instrument, StringComparison.OrdinalIgnoreCase)
            && AssetTypes.IsFutures(a.Venue.AssetType) == futuresVenue);
    }

    public string Resolve(string venue, string instrument) =>
        Find(venue, instrument) is { } asset
            ? Path.Combine(baseDir, asset.Exchange, asset.Venue.Dir)
            : Path.Combine(baseDir, venue, instrument);

    public string VenueDir(string venue) => Path.Combine(baseDir, venue);

    /// <summary>Per-instrument tick scale (DecimalDigits) from the session plan; null when unconfigured.</summary>
    public int? ResolveDigits(string venue, string instrument) =>
        Find(venue, instrument)?.DecimalDigits;
}
