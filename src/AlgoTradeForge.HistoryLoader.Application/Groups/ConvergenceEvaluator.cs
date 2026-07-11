using System.Globalization;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

public sealed class ConvergenceEvaluator(IHistoryIndex index, SymbologyRegistry registry)
{
    /// <summary>Dry-run diff (phase-2 subset). Coarse month counting: covered = month_partitions
    /// row exists / CompleteMonths contains month; expected = months from HistoryStart..now UTC
    /// inclusive. Status rules IN ORDER: unsupported → Venue null; on-demand → (Collect ==
    /// "on-demand" OR Tuple.IsDerived — phase 2 cannot materialize derived regardless of their
    /// materialize value) AND 0 covered — an expected state, NOT a discrepancy; missing →
    /// 0 covered AND expected > 0 (future historyStart ⇒ expected 0 ⇒ vacuously converged, not
    /// missing); materialized → covered ≥ expected; else partial. Orphans:
    /// every index ListAssets×ListFeedKeys key not claimed by any tuple (match on (exchange
    /// OrdinalIgnoreCase — tuples are lowercase, index rows may not be, dir, feedName, interval));
    /// the equity root will be almost entirely orphaned in phase 2 — expected and correct
    /// (nothing declares it yet).</summary>
    public Task<ConvergenceReport> Evaluate(IReadOnlyList<CollectionGroup> groups, CancellationToken ct = default)
        => Evaluate(groups, DateOnly.FromDateTime(DateTime.UtcNow), ct);

    internal async Task<ConvergenceReport> Evaluate(
        IReadOnlyList<CollectionGroup> groups, DateOnly nowMonth, CancellationToken ct = default)
    {
        var nowFirst = new DateOnly(nowMonth.Year, nowMonth.Month, 1);
        var state = GroupExpansion.Expand(groups, registry);

        var tupleStatuses = new List<TupleStatus>(state.Tuples.Count);

        var feedStatusCache = new Dictionary<(string Exchange, string Dir), IReadOnlyList<FeedStatusIndexRow>>();
        var monthsCache = new Dictionary<(string Exchange, string Dir, string FeedName, string Interval), IReadOnlyList<MonthPartitionRow>>();
        // feedKeysCache built lazily; reused by orphan detection to avoid double round-trips.
        var feedKeysCache = new Dictionary<(string Exchange, string Dir), IReadOnlyList<(string FeedName, string Interval)>>();

        foreach (var tuple in state.Tuples)
        {
            // Rule 1: unsupported → Venue null
            if (tuple.Venue is null)
            {
                tupleStatuses.Add(new TupleStatus(tuple, "unsupported", 0, 0));
                continue;
            }

            var expected = CountExpectedMonths(tuple.HistoryStart, nowFirst);
            int covered;

            if (tuple.FeedName == FeedNames.Candles)
            {
                // candles: exact interval match via month_partitions
                var monthsKey = (tuple.Exchange, tuple.Venue.Dir, tuple.FeedName, tuple.Interval);
                if (!monthsCache.TryGetValue(monthsKey, out var months))
                {
                    months = await index.GetMonths(tuple.Exchange, tuple.Venue.Dir, tuple.FeedName, tuple.Interval, ct);
                    monthsCache[monthsKey] = months;
                }
                covered = months.Count;
            }
            else if (!tuple.IsDerived)
            {
                // non-candles collected: union coverage across ALL cadence intervals in the index.
                // GroupExpansion emits Interval="" for every non-candles feed; the real rows may be
                // at a cadence interval (e.g. "1h" for mark-price, "5m" for open-interest).
                var cacheKey = (tuple.Exchange, tuple.Venue.Dir);
                if (!feedKeysCache.TryGetValue(cacheKey, out var feedKeys))
                {
                    feedKeys = await index.ListFeedKeys(tuple.Exchange, tuple.Venue.Dir, ct);
                    feedKeysCache[cacheKey] = feedKeys;
                }

                var coveredMonths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (fn, interval) in feedKeys)
                {
                    if (fn != tuple.FeedName) continue;

                    if (string.IsNullOrEmpty(interval))
                    {
                        // interval-less (e.g. funding-rate): use feed_status CompleteMonths
                        var statusKey = (tuple.Exchange, tuple.Venue.Dir);
                        if (!feedStatusCache.TryGetValue(statusKey, out var statuses))
                        {
                            statuses = await index.GetFeedStatuses(tuple.Exchange, tuple.Venue.Dir, ct);
                            feedStatusCache[statusKey] = statuses;
                        }
                        var fs = statuses.FirstOrDefault(s => s.FeedName == fn && s.Interval == "");
                        if (fs is not null)
                        {
                            var cms = JsonSerializer.Deserialize<string[]>(fs.CompleteMonthsJson, ManifestJson.Options) ?? [];
                            foreach (var m in cms) coveredMonths.Add(m);
                        }
                    }
                    else
                    {
                        // interval-based (e.g. mark-price "1h", open-interest "5m"): use month_partitions
                        var monthsKey = (tuple.Exchange, tuple.Venue.Dir, fn, interval);
                        if (!monthsCache.TryGetValue(monthsKey, out var months))
                        {
                            months = await index.GetMonths(tuple.Exchange, tuple.Venue.Dir, fn, interval, ct);
                            monthsCache[monthsKey] = months;
                        }
                        foreach (var row in months) coveredMonths.Add(row.Month);
                    }
                }
                covered = coveredMonths.Count;
            }
            else
            {
                // derived: interval-less, feed_status path (phase 2: always 0 → on-demand)
                var cacheKey = (tuple.Exchange, tuple.Venue.Dir);
                if (!feedStatusCache.TryGetValue(cacheKey, out var statuses))
                {
                    statuses = await index.GetFeedStatuses(tuple.Exchange, tuple.Venue.Dir, ct);
                    feedStatusCache[cacheKey] = statuses;
                }
                var feedStatus = statuses.FirstOrDefault(s =>
                    s.FeedName == tuple.FeedName && s.Interval == "");
                covered = feedStatus is null
                    ? 0
                    : (JsonSerializer.Deserialize<string[]>(feedStatus.CompleteMonthsJson, ManifestJson.Options) ?? []).Length;
            }

            // Rule 2: on-demand → (Collect == "on-demand" OR IsDerived) AND 0 covered
            if ((tuple.Collect == "on-demand" || tuple.IsDerived) && covered == 0)
            {
                tupleStatuses.Add(new TupleStatus(tuple, "on-demand", expected, covered));
                continue;
            }

            // Rule 3: missing → 0 covered AND expected > 0
            if (covered == 0 && expected > 0)
            {
                tupleStatuses.Add(new TupleStatus(tuple, "missing", expected, covered));
                continue;
            }

            // Rule 4: materialized → covered ≥ expected
            if (covered >= expected)
            {
                tupleStatuses.Add(new TupleStatus(tuple, "materialized", expected, covered));
                continue;
            }

            // Rule 5: else partial
            tupleStatuses.Add(new TupleStatus(tuple, "partial", expected, covered));
        }

        // Orphan detection: every index (exchange, dir, feedName, interval) not claimed by any tuple.
        // candles tuples claim exact key + candle-ext at same interval (side-output of CandleFeedCollector).
        // non-candles collected tuples claim any interval for their feedName (cadence interval varies by feed).
        // derived tuples keep exact match (rows arrive in phase 3).
        var claimedExactKeys = new HashSet<(string Exchange, string Dir, string FeedName, string Interval)>();
        var claimedFeedNames = new HashSet<(string Exchange, string Dir, string FeedName)>();

        foreach (var tuple in state.Tuples)
        {
            if (tuple.Venue is null) continue;
            var ex = tuple.Exchange; // already lowercase (GroupExpansion normalizes on entry)
            var dir = tuple.Venue.Dir;

            if (tuple.FeedName == FeedNames.Candles)
            {
                claimedExactKeys.Add((ex, dir, tuple.FeedName, tuple.Interval));
                // candle-ext is a side-output written by CandleFeedCollector alongside candles
                claimedExactKeys.Add((ex, dir, FeedNames.CandleExt, tuple.Interval));
            }
            else if (!tuple.IsDerived)
            {
                claimedFeedNames.Add((ex, dir, tuple.FeedName));
            }
            else
            {
                claimedExactKeys.Add((ex, dir, tuple.FeedName, tuple.Interval));
            }
        }

        var allAssets = await index.ListAssets(ct: ct);
        var orphans = new List<OrphanEntry>();

        foreach (var asset in allAssets)
        {
            var cacheKey = (asset.Exchange.ToLowerInvariant(), asset.Dir);
            if (!feedKeysCache.TryGetValue(cacheKey, out var feedKeys))
            {
                feedKeys = await index.ListFeedKeys(asset.Exchange, asset.Dir, ct);
                feedKeysCache[cacheKey] = feedKeys;
            }

            foreach (var (feedName, interval) in feedKeys)
            {
                // OrdinalIgnoreCase on exchange: index rows may not be lowercase
                var exLower = asset.Exchange.ToLowerInvariant();
                var exactKey = (exLower, asset.Dir, feedName, interval);
                var feedNameKey = (exLower, asset.Dir, feedName);
                if (!claimedExactKeys.Contains(exactKey) && !claimedFeedNames.Contains(feedNameKey))
                    orphans.Add(new OrphanEntry(asset.Exchange, asset.Dir, feedName, interval));
            }
        }

        return new ConvergenceReport(DateTimeOffset.UtcNow, tupleStatuses, orphans, state.Conflicts);
    }

    private static int CountExpectedMonths(string historyStart, DateOnly nowFirst)
    {
        if (!DateOnly.TryParseExact(historyStart, "yyyy-MM",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            return 0;
        start = new DateOnly(start.Year, start.Month, 1);
        if (start > nowFirst) return 0;
        return (nowFirst.Year - start.Year) * 12 + (nowFirst.Month - start.Month) + 1;
    }
}
