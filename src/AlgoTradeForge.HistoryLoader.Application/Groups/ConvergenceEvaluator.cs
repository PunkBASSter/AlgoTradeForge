using System.Globalization;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

public sealed class ConvergenceEvaluator(IHistoryIndex index, SymbologyRegistry registry)
{
    /// <summary>Dry-run diff. Coarse month counting: covered = month_partitions row exists /
    /// CompleteMonths contains month; expected = months from EffectiveStart..now UTC inclusive,
    /// where EffectiveStart = max(HistoryStart, discovered-first-month, and — for stream feeds —
    /// first observed month). Status rules IN ORDER: unsupported → Venue null; blocked →
    /// (Exchange, Venue.Dir) ∈ blocked set (no instrument scale etc.); on-demand → (Collect ==
    /// "on-demand" OR Tuple.IsDerived — phase 2/3a cannot materialize derived) AND 0 covered;
    /// awaiting-data → stream feed (liquidations / book-ticker) AND 0 covered (no backfill exists,
    /// data only accrues live); missing → 0 covered AND expected > 0 (future EffectiveStart ⇒
    /// expected 0 ⇒ vacuously converged); materialized → covered ≥ expected; else partial. Orphans:
    /// every index feed key (one ListAllFeedKeys call) not claimed by any tuple (exchange compared
    /// case-insensitively — index rows may not be lowercase; dir/feed/interval Ordinal).</summary>
    public Task<ConvergenceReport> Evaluate(IReadOnlyList<CollectionGroup> groups, CancellationToken ct = default)
        => Evaluate(groups, DateOnly.FromDateTime(DateTime.UtcNow), ct);

    internal Task<ConvergenceReport> Evaluate(
        IReadOnlyList<CollectionGroup> groups, DateOnly nowMonth, CancellationToken ct = default)
        => Evaluate(GroupExpansion.Expand(groups, registry), [], nowMonth, ct);

    public Task<ConvergenceReport> Evaluate(
        DesiredState state, IReadOnlyList<BlockedAsset> blocked, CancellationToken ct = default)
        => Evaluate(state, blocked, DateOnly.FromDateTime(DateTime.UtcNow), ct);

    internal async Task<ConvergenceReport> Evaluate(
        DesiredState state, IReadOnlyList<BlockedAsset> blocked, DateOnly nowMonth, CancellationToken ct = default)
    {
        var nowFirst = new DateOnly(nowMonth.Year, nowMonth.Month, 1);

        var blockedSet = new HashSet<(string Exchange, string Dir)>();
        foreach (var b in blocked)
            blockedSet.Add((b.Exchange, b.Dir));

        // Discovery clamp lookups (fetched once). candles → exact (feed, interval); non-candles →
        // earliest month across any interval for the feed.
        var discoveryExact = new Dictionary<(string Exchange, string Dir, string FeedName, string Interval), string>();
        var discoveryByFeed = new Dictionary<(string Exchange, string Dir, string FeedName), string>();
        foreach (var d in await index.ListDiscoveredFirstMonths(ct))
        {
            var exLower = d.Exchange.ToLowerInvariant();
            discoveryExact[(exLower, d.Dir, d.FeedName, d.Interval)] = d.Month;
            var feedKey = (exLower, d.Dir, d.FeedName);
            if (!discoveryByFeed.TryGetValue(feedKey, out var cur) || string.CompareOrdinal(d.Month, cur) < 0)
                discoveryByFeed[feedKey] = d.Month;
        }

        // One bulk read serves both coverage (non-candles any-interval union) and orphan scan.
        var feedKeysByAsset = new Dictionary<(string ExLower, string Dir), (string ExOrig, List<(string FeedName, string Interval)> Keys)>();
        foreach (var (ex, dir, fn, iv) in await index.ListAllFeedKeys(ct))
        {
            var key = (ex.ToLowerInvariant(), dir);
            if (!feedKeysByAsset.TryGetValue(key, out var entry))
                feedKeysByAsset[key] = entry = (ex, []);
            entry.Keys.Add((fn, iv));
        }

        var tupleStatuses = new List<TupleStatus>(state.Tuples.Count);

        var feedStatusCache = new Dictionary<(string Exchange, string Dir), IReadOnlyList<FeedStatusIndexRow>>();
        var monthsCache = new Dictionary<(string Exchange, string Dir, string FeedName, string Interval), IReadOnlyList<MonthPartitionRow>>();

        foreach (var tuple in state.Tuples)
        {
            // Rule 1: unsupported → Venue null
            if (tuple.Venue is null)
            {
                tupleStatuses.Add(new TupleStatus(tuple, "unsupported", 0, 0));
                continue;
            }

            // Rule 2: blocked → (Exchange, Venue.Dir) ∈ blocked set
            if (blockedSet.Contains((tuple.Exchange, tuple.Venue.Dir)))
            {
                tupleStatuses.Add(new TupleStatus(tuple, "blocked", 0, 0));
                continue;
            }

            int covered;
            HashSet<string>? coveredMonths = null;

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
                coveredMonths = new HashSet<string>(StringComparer.Ordinal);
                feedKeysByAsset.TryGetValue((tuple.Exchange, tuple.Venue.Dir), out var assetFeeds);
                foreach (var (fn, interval) in assetFeeds.Keys ?? [])
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
                // derived: interval-less, feed_status path (phase 3a: always 0 → on-demand)
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

            var effectiveStart = tuple.HistoryStart;
            var discovered = tuple.FeedName == FeedNames.Candles
                ? (discoveryExact.TryGetValue((tuple.Exchange, tuple.Venue.Dir, tuple.FeedName, tuple.Interval), out var dm) ? dm : null)
                : (discoveryByFeed.TryGetValue((tuple.Exchange, tuple.Venue.Dir, tuple.FeedName), out var dm2) ? dm2 : null);
            if (discovered is not null) effectiveStart = MaxMonth(effectiveStart, discovered);

            // Stream feeds never backfill: with observed months, count expected from first observed.
            var isStream = tuple.FeedName is FeedNames.Liquidations or FeedNames.BookTicker;
            if (isStream && coveredMonths is { Count: > 0 })
                effectiveStart = MaxMonth(effectiveStart, coveredMonths.Min(StringComparer.Ordinal)!);

            var expected = CountExpectedMonths(effectiveStart, nowFirst);

            // Rule 3: on-demand → (Collect == "on-demand" OR IsDerived) AND 0 covered
            if ((tuple.Collect == "on-demand" || tuple.IsDerived) && covered == 0)
            {
                tupleStatuses.Add(new TupleStatus(tuple, "on-demand", expected, covered));
                continue;
            }

            // Rule 4: awaiting-data → stream feed AND 0 covered (live-only, no backfill)
            if (isStream && covered == 0)
            {
                tupleStatuses.Add(new TupleStatus(tuple, "awaiting-data", expected, covered));
                continue;
            }

            // Rule 5: missing → 0 covered AND expected > 0
            if (covered == 0 && expected > 0)
            {
                tupleStatuses.Add(new TupleStatus(tuple, "missing", expected, covered));
                continue;
            }

            // Rule 6: materialized → covered ≥ expected
            if (covered >= expected)
            {
                tupleStatuses.Add(new TupleStatus(tuple, "materialized", expected, covered));
                continue;
            }

            // Rule 7: else partial
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

        var orphans = new List<OrphanEntry>();
        foreach (var ((exLower, dir), (exOrig, keys)) in feedKeysByAsset)
        {
            foreach (var (feedName, interval) in keys)
            {
                var exactKey = (exLower, dir, feedName, interval);
                var feedNameKey = (exLower, dir, feedName);
                if (!claimedExactKeys.Contains(exactKey) && !claimedFeedNames.Contains(feedNameKey))
                    orphans.Add(new OrphanEntry(exOrig, dir, feedName, interval));
            }
        }

        return new ConvergenceReport(DateTimeOffset.UtcNow, tupleStatuses, orphans, state.Conflicts);
    }

    // "yyyy-MM" strings compare correctly under Ordinal (fixed width, zero-padded).
    private static string MaxMonth(string a, string b) => string.CompareOrdinal(a, b) >= 0 ? a : b;

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
