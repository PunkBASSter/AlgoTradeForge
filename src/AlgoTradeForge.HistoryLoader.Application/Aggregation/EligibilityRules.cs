using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Source/type compatibility matrix used by <c>GET .../feeds/{feedId}/aggregation-options</c>
/// and the <c>POST /aggregate</c> validation step. Pure function; no I/O.
/// </summary>
public static class EligibilityRules
{
    private static readonly string[] AllAltBarTypes =
        ["EqT", "EqV", "EqD", "EqI", "Range", "Renko"];

    // Range/Renko require a tick source: time-bar collapses force a one-emit-per-record
    // approximation that distorts actual_overshoot_pct.
    private const string RangeRenkoRequiresTickReason =
        "Range/Renko require a tick source for fidelity in v1.";

    // Re-aggregation safe-trio. EqV/EqT/EqD compose cleanly when the source is the same type
    // with a smaller threshold (contributions sum linearly across pre-aggregated bars). EqI
    // loses internal trajectory; Range/Renko are path-dependent on individual ticks.
    private static readonly IReadOnlySet<string> SafeReaggregationTypes =
        new HashSet<string>(StringComparer.Ordinal) { "EqV", "EqT", "EqD" };

    private const string EqIReaggregationReason =
        "EqI re-aggregation deferred — collapses internal signed trajectory and requires a .flow sidecar reader.";

    private const string PathDependentReaggregationReason =
        "Source type's bar shape is path-dependent on individual ticks; cannot be re-aggregated.";

    private const string CrossFamilyReaggregationReason =
        "Re-aggregation must stay within the same type family (EqV→EqV, EqT→EqT, EqD→EqD).";

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
                Allow(["EqT", "EqV", "EqD", "EqI"],
                    ineligible: [
                        ("Range", RangeRenkoRequiresTickReason),
                        ("Renko", RangeRenkoRequiresTickReason),
                    ],
                    warning: AltBarWarnings.TimeBarEqIProxy),

            SourceKind.TimeBarWithVolume when hasCandleExt /* spot */ =>
                Allow(["EqT", "EqV", "EqD"],
                    ineligible: [
                        ("EqI", "EqI requires perp/future asset for taker-buy proxy."),
                        ("Range", RangeRenkoRequiresTickReason),
                        ("Renko", RangeRenkoRequiresTickReason),
                    ]),

            SourceKind.TimeBarWithVolume /* no candle-ext */ =>
                Allow(["EqT", "EqV", "EqD"],
                    ineligible: [
                        ("EqI", "EqI requires either a tick source or candle-ext on the time-bar source."),
                        ("Range", RangeRenkoRequiresTickReason),
                        ("Renko", RangeRenkoRequiresTickReason),
                    ]),

            SourceKind.OhlcOnly =>
                Allow([],
                    ineligible: [
                        ("EqT", "OHLC-only sources have no volume column."),
                        ("EqV", "OHLC-only sources have no volume column."),
                        ("EqD", "OHLC-only sources have no volume column."),
                        ("EqI", "OHLC-only sources have no volume column."),
                        ("Range", RangeRenkoRequiresTickReason),
                        ("Renko", RangeRenkoRequiresTickReason),
                    ]),

            SourceKind.AltBar => AltBarReaggregation(source),

            SourceKind.Side =>
                Allow([],
                    ineligible: AllAltBarTypes.Select(t =>
                        (t, "Side feeds cannot be aggregated.")).ToArray()),

            _ => Allow([], ineligible: [("Unknown", $"Unrecognized source kind: {source.Kind}")]),
        };
    }

    private static EligibilityResult Allow(
        string[] eligible,
        (string code, string reason)[] ineligible,
        string? warning = null) => new(
            EligibleTypes: eligible,
            IneligibleTypes: ineligible.Select(t => new IneligibleType(t.code, t.reason)).ToArray(),
            Warnings: warning is null ? [] : [warning]);

    private static bool IsPerpOrFuture(string assetType) => AssetTypes.IsFutures(assetType);

    // Re-aggregation eligibility from an existing alt-bar source. Within the safe trio
    // (EqV/EqT/EqD) only the same type code is eligible. EqI/Range/Renko sources reject all
    // output types. Threshold ordering is checked at the POST /aggregate endpoint.
    private static EligibilityResult AltBarReaggregation(FeedDefinition source)
    {
        var sourceTypeCode = source.Type?.Code;

        // Defense-in-depth: malformed alt-bar entry without Type field can't be re-aggregated.
        // Manifest writer always populates Type.Code, so this is unreachable for well-formed feeds.
        if (string.IsNullOrEmpty(sourceTypeCode))
        {
            return Allow([],
                ineligible: AllAltBarTypes.Select(t =>
                    (t, "Source alt-bar entry is missing type metadata — cannot determine re-aggregation eligibility.")).ToArray());
        }

        if (sourceTypeCode is "Range" or "Renko")
        {
            return Allow([],
                ineligible: AllAltBarTypes.Select(t =>
                    (t, PathDependentReaggregationReason)).ToArray());
        }

        if (sourceTypeCode == "EqI")
        {
            return Allow([],
                ineligible: AllAltBarTypes.Select(t =>
                    (t, EqIReaggregationReason)).ToArray());
        }

        if (!SafeReaggregationTypes.Contains(sourceTypeCode))
        {
            // Unknown / future type — fail closed.
            return Allow([],
                ineligible: AllAltBarTypes.Select(t =>
                    (t, $"Re-aggregation from source type '{sourceTypeCode}' is not supported.")).ToArray());
        }

        var eligible = new[] { sourceTypeCode };
        var ineligible = AllAltBarTypes
            .Where(t => t != sourceTypeCode)
            .Select(t => (t, CrossFamilyReaggregationReason))
            .ToArray();
        return Allow(eligible, ineligible);
    }

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
