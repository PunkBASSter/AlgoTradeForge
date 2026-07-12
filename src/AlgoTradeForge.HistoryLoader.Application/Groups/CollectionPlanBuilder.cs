using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

/// <summary>Pure projection from desired state + index data → <see cref="CollectionPlan"/>. No I/O.</summary>
public static class CollectionPlanBuilder
{
    private static readonly DateOnly DefaultHistoryStart = new(2017, 1, 1);

    public static CollectionPlan Build(
        DesiredState state,
        IReadOnlyList<DiscoveredFirstMonthRow> discovered,
        IReadOnlyList<InstrumentMetaRow> meta,
        IReadOnlyDictionary<(string Exchange, string Dir), int> recordedDigits)
    {
        var assets = new List<CollectionAsset>();
        var blocked = new List<BlockedAsset>();
        var warnings = new List<PlanWarning>();

        // Rule 1: skip Venue==null (unsupported) and IsDerived (materialization is phase 3b)
        var active = state.Tuples.Where(t => t.Venue is not null && !t.IsDerived);

        // Rule 2: group by (Exchange, Venue)
        foreach (var group in active.GroupBy(t => (Exchange: t.Exchange, Venue: t.Venue!)))
        {
            var exchange = group.Key.Exchange;
            var venue = group.Key.Venue;
            var dir = venue.Dir;
            var canonical = group.First().Canonical;

            // Rule 3+4: decimal digits resolution
            var hasRecorded = recordedDigits.TryGetValue((exchange, dir), out var recorded);
            var metaRow = meta.FirstOrDefault(m =>
                string.Equals(m.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.Dir, dir, StringComparison.OrdinalIgnoreCase));

            if (!hasRecorded && metaRow is null)
            {
                blocked.Add(new BlockedAsset(exchange, canonical, dir,
                    "instrument precision unknown (exchangeInfo unavailable or symbol absent)"));
                continue;
            }

            int decimalDigits;
            if (hasRecorded)
            {
                decimalDigits = recorded;
                // Rule 4: warn when both sources agree to disagree
                if (metaRow is not null && metaRow.PriceDecimals != recorded)
                    warnings.Add(new PlanWarning(exchange, dir,
                        $"disk scale {recorded} != exchangeInfo {metaRow.PriceDecimals} — venue tickSize drifted; disk governs writes"));
            }
            else
            {
                decimalDigits = metaRow!.PriceDecimals;
            }

            // Rule 5+6: build feeds sorted by (FeedName, Interval) Ordinal
            var feeds = group
                .Select(t => BuildFeed(t, exchange, dir, discovered))
                .OrderBy(f => f.FeedName, StringComparer.Ordinal)
                .ThenBy(f => f.Interval, StringComparer.Ordinal)
                .ToList();

            assets.Add(new CollectionAsset(exchange, canonical, venue, decimalDigits, feeds));
        }

        // Rule 6: assets sorted Ordinal by (Exchange, Dir)
        var sortedAssets = assets
            .OrderBy(a => a.Exchange, StringComparer.Ordinal)
            .ThenBy(a => a.Venue.Dir, StringComparer.Ordinal)
            .ToList();

        return new CollectionPlan(sortedAssets, blocked, warnings);
    }

    private static CollectionFeed BuildFeed(
        DesiredTuple t,
        string exchange,
        string dir,
        IReadOnlyList<DiscoveredFirstMonthRow> discovered)
    {
        var historyStart = ParseMonth(t.HistoryStart, DefaultHistoryStart);

        // Rule 5: clamp to earliest discovered across all intervals for this (exchange, dir, feedName)
        var earliestDiscovered = discovered
            .Where(d =>
                string.Equals(d.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Dir, dir, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.FeedName, t.FeedName, StringComparison.OrdinalIgnoreCase))
            .Select(d => ParseMonth(d.Month, DateOnly.MinValue))
            .DefaultIfEmpty(DateOnly.MinValue)
            .Min();

        // EffectiveStart = max(historyStart, earliestDiscovered); no discovery → no clamp
        var effectiveStart = earliestDiscovered > DateOnly.MinValue && earliestDiscovered > historyStart
            ? earliestDiscovered
            : historyStart;

        // groups declare membership; the disk cadence is collector-owned (FeedCadence)
        var interval = string.IsNullOrEmpty(t.Interval) ? FeedCadence.DiskInterval(t.FeedName) : t.Interval;
        return new CollectionFeed(t.FeedName, interval, t.Collect, t.Format, effectiveStart);
    }

    private static DateOnly ParseMonth(string value, DateOnly fallback)
    {
        if (value.Length == 7 && value[4] == '-'
            && int.TryParse(value.AsSpan(0, 4), out var year)
            && int.TryParse(value.AsSpan(5, 2), out var month)
            && month >= 1 && month <= 12)
            return new DateOnly(year, month, 1);
        return fallback;
    }
}
