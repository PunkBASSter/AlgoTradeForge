namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

/// <summary>
/// Resolves a relay instrument string (e.g. "BTCUSDT") to the canonical asset directory the
/// CSV writers partition under. Defaults to <c>{baseDir}/{venue}/{instrument}</c>; explicit
/// overrides (keyed by instrument) carry venue-specific naming such as the <c>_perp</c> suffix.
/// </summary>
public sealed class InstrumentAssetDirMap(
    string baseDir,
    IReadOnlyDictionary<string, string> overrides,
    IReadOnlyDictionary<string, int>? decimalDigits = null)
{
    public string Resolve(string venue, string instrument) =>
        overrides.TryGetValue(instrument, out var dir)
            ? Path.Combine(baseDir, dir)
            : Path.Combine(baseDir, venue, instrument);

    public string VenueDir(string venue) => Path.Combine(baseDir, venue);

    /// <summary>Per-instrument tick scale (DecimalDigits); null when unconfigured.</summary>
    public int? ResolveDigits(string instrument) =>
        decimalDigits is not null && decimalDigits.TryGetValue(instrument, out var d) ? d : null;
}
