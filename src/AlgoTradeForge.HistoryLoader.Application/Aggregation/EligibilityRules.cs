using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Encodes the TRD §7 source/type compatibility matrix used by
/// <c>GET .../feeds/{feedId}/aggregation-options</c> and the <c>POST /aggregate</c>
/// validation step. Pure function; no I/O.
/// </summary>
public static class EligibilityRules
{
    private static readonly string[] AllAltBarTypes =
        ["EqT", "EqV", "EqD", "EqI", "Range", "Renko"];

    public sealed record EligibilityResult(
        IReadOnlyList<string> EligibleTypes,
        IReadOnlyList<IneligibleType> IneligibleTypes,
        IReadOnlyList<string> Warnings);

    public sealed record IneligibleType(string Code, string Reason);

    /// <summary>
    /// Returns the eligible alt-bar types for the given feed in this asset's catalog. Caller
    /// supplies the source feed's <see cref="FeedDefinition"/>, the asset type code (e.g.
    /// <c>"CryptoPerpetual"</c>), and whether the asset has a <c>candle-ext</c> feed entry.
    /// </summary>
    public static EligibilityResult ForSource(
        FeedDefinition source,
        string assetType,
        bool hasCandleExt)
    {
        var sourceKind = ResolveKind(source);

        return sourceKind switch
        {
            SourceKind.Tick => Allow(AllAltBarTypes, []),

            SourceKind.TimeBarWithVolume when hasCandleExt && IsPerpOrFuture(assetType) =>
                Allow(["EqT", "EqV", "EqD", "EqI"], [],
                    warning: AltBarWarnings.TimeBarEqIProxy),

            SourceKind.TimeBarWithVolume when hasCandleExt /* spot */ =>
                Allow(["EqT", "EqV", "EqD"],
                    ineligible: [("EqI", "EqI requires perp/future asset for taker-buy proxy.")]),

            SourceKind.TimeBarWithVolume /* no candle-ext */ =>
                Allow(["EqT", "EqV", "EqD"],
                    ineligible: [("EqI", "EqI requires either a tick source or candle-ext on the time-bar source.")]),

            SourceKind.OhlcOnly =>
                Allow([],
                    ineligible: [
                        ("EqT", "OHLC-only sources have no volume column."),
                        ("EqV", "OHLC-only sources have no volume column."),
                        ("EqD", "OHLC-only sources have no volume column."),
                        ("EqI", "OHLC-only sources have no volume column."),
                    ]),

            SourceKind.AltBar =>
                Allow([],
                    ineligible: AllAltBarTypes.Select(t =>
                        (t, "Re-aggregation from alt-bar sources is not supported in v1 (Phase 6).")).ToArray()),

            SourceKind.Side =>
                Allow([],
                    ineligible: AllAltBarTypes.Select(t =>
                        (t, "Side feeds cannot be aggregated.")).ToArray()),

            _ => Allow([], ineligible: [("Unknown", $"Unrecognized source kind: {source.Kind}")]),
        };

        static EligibilityResult Allow(
            string[] eligible,
            (string code, string reason)[] ineligible,
            string? warning = null) => new(
                EligibleTypes: eligible,
                IneligibleTypes: ineligible.Select(t => new IneligibleType(t.code, t.reason)).ToArray(),
                Warnings: warning is null ? [] : [warning]);
    }

    private static bool IsPerpOrFuture(string assetType) => AssetTypes.IsFutures(assetType);

    private static SourceKind ResolveKind(FeedDefinition feed)
    {
        if (string.Equals(feed.Kind, "OHLCV_AltBar", StringComparison.Ordinal))
            return SourceKind.AltBar;
        if (string.Equals(feed.Kind, "Tick", StringComparison.Ordinal))
            return SourceKind.Tick;
        if (string.Equals(feed.Kind, "Side", StringComparison.Ordinal))
            return SourceKind.Side;

        // Time-bar (legacy entries leave Kind null and rely on Interval). OHLCV vs OHLC-only
        // is determined by presence of a volume column.
        var hasVolume = feed.Columns.Length == 0 || feed.Columns.Any(c =>
            string.Equals(c, "vol", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c, "volume", StringComparison.OrdinalIgnoreCase));

        return hasVolume ? SourceKind.TimeBarWithVolume : SourceKind.OhlcOnly;
    }

    private enum SourceKind
    {
        Tick,
        TimeBarWithVolume,
        OhlcOnly,
        AltBar,
        Side,
    }
}
