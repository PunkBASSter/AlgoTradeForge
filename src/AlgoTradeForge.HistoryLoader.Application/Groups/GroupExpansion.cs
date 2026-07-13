using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

public static class GroupExpansion
{
    /// <summary>Pure. Normalizes every exchange id to lowercase ON ENTRY (ToLowerInvariant —
    /// belt-and-suspenders behind the validator's lowercase rule; DesiredTuple.Exchange is always
    /// lowercase, so all downstream comparisons are plain Ordinal). Expands enabled groups ×
    /// exchanges × symbols × feeds (+derived), resolves symbology (overrides first, then registry;
    /// unknown exchange → unsupported "no symbology"), merges duplicates deterministically: eager
    /// beats on-demand, historyStart = min, groups accumulated sorted. Non-mergeable conflicts
    /// (same physical feed different format; same derived name different source/type/threshold)
    /// land in Conflicts — expansion still returns the rest. Unsupported deduped by
    /// (Exchange, Canonical). Disabled groups contribute nothing.</summary>
    public static DesiredState Expand(IReadOnlyList<CollectionGroup> groups, SymbologyRegistry registry)
    {
        var rawTuples = new List<RawTuple>();
        var unsupportedKeys = new HashSet<(string Exchange, string Canonical)>();
        var unsupportedList = new List<UnsupportedTuple>();

        foreach (var group in groups)
        {
            if (!group.Enabled) continue;

            foreach (var rawExchange in group.Exchanges)
            {
                var exchange = rawExchange.ToLowerInvariant();
                var symbology = registry.Get(exchange);

                foreach (var symbolStr in group.Assets.Symbols)
                {
                    if (!CanonicalSymbolParser.TryParse(symbolStr, out var canonical, out _))
                        continue;

                    if (symbology is null)
                    {
                        AddUnsupported(unsupportedKeys, unsupportedList,
                            exchange, canonical!.ToString(), $"no symbology for exchange '{exchange}'");
                        continue;
                    }

                    // overrides replace ApiSymbol only; dir/type still from symbology
                    string? overrideApiSymbol = null;
                    if (group.SymbolOverrides?.TryGetValue(exchange, out var exchangeOverrides) == true)
                        exchangeOverrides?.TryGetValue(symbolStr, out overrideApiSymbol);

                    if (!symbology.TryResolve(canonical!, out var venue, out var reason))
                    {
                        AddUnsupported(unsupportedKeys, unsupportedList,
                            exchange, canonical!.ToString(), reason!);
                        continue;
                    }

                    if (overrideApiSymbol is not null)
                        venue = venue! with { ApiSymbol = overrideApiSymbol };

                    foreach (var (feedName, feedDef) in group.Feeds)
                    {
                        var format = feedDef.Format ?? "csv";

                        if (feedName == FeedNames.Candles)
                        {
                            foreach (var interval in feedDef.Intervals ?? [])
                            {
                                rawTuples.Add(new RawTuple(
                                    exchange, canonical!.ToString(), venue!,
                                    feedName, interval,
                                    feedDef.Collect, format,
                                    group.Assets.HistoryStart, false, group.Name,
                                    null, null, null));
                            }
                        }
                        else
                        {
                            rawTuples.Add(new RawTuple(
                                exchange, canonical!.ToString(), venue!,
                                feedName, string.Empty,
                                feedDef.Collect, format,
                                group.Assets.HistoryStart, false, group.Name,
                                null, null, null));
                        }
                    }

                    // derived alt-bars are always csv in phase 2
                    if (group.Derived is not null)
                    {
                        foreach (var (derivedId, derivedDef) in group.Derived)
                        {
                            rawTuples.Add(new RawTuple(
                                exchange, canonical!.ToString(), venue!,
                                derivedId, string.Empty,
                                derivedDef.Materialize, "csv",
                                group.Assets.HistoryStart, true, group.Name,
                                derivedDef.Source, derivedDef.Type, derivedDef.Threshold));
                        }
                    }
                }
            }
        }

        // Merge key: (Exchange, Venue.Dir, FeedName, Interval) — safe as plain Ordinal because Exchange is lowercase-normalized on entry
        var resultTuples = new List<DesiredTuple>();
        var conflicts = new List<GroupConflict>();

        foreach (var mg in rawTuples.GroupBy(t => (t.Exchange, t.Venue.Dir, t.FeedName, t.Interval)))
        {
            var items = mg.ToList();
            var first = items[0];
            var key = $"{first.Exchange}/{first.Venue.Dir}/{first.FeedName}/{first.Interval}";

            if (first.IsDerived)
            {
                var def0 = (first.DerivedSource, first.DerivedType, first.DerivedThreshold);
                if (items.Any(i => (i.DerivedSource, i.DerivedType, i.DerivedThreshold) != def0))
                {
                    var cGroups = items.Select(i => i.Group).Distinct()
                        .OrderBy(g => g, StringComparer.Ordinal).ToList();
                    conflicts.Add(new GroupConflict(key, "derived-definition", cGroups,
                        $"derived '{first.FeedName}' has conflicting definitions across groups: {string.Join(", ", cGroups)}"));
                    continue;
                }
            }
            else
            {
                var formats = items.Select(i => i.Format).Distinct().ToList();
                if (formats.Count > 1)
                {
                    var cGroups = items.Select(i => i.Group).Distinct()
                        .OrderBy(g => g, StringComparer.Ordinal).ToList();
                    conflicts.Add(new GroupConflict(key, "format", cGroups,
                        $"feed '{first.FeedName}' formats [{string.Join(", ", formats)}] conflict across groups: {string.Join(", ", cGroups)}"));
                    continue;
                }
            }

            var collect = items.Any(i => i.Collect == "eager") ? "eager" : "on-demand";
            var historyStart = items.Select(i => i.HistoryStart)
                .OrderBy(s => s, StringComparer.Ordinal).First();
            var groupNames = items.Select(i => i.Group).Distinct()
                .OrderBy(g => g, StringComparer.Ordinal).ToList();

            resultTuples.Add(new DesiredTuple(
                Exchange: first.Exchange,
                Canonical: first.Canonical,
                Venue: first.Venue,
                FeedName: first.FeedName,
                Interval: first.Interval,
                Collect: collect,
                Format: first.Format,
                HistoryStart: historyStart,
                IsDerived: first.IsDerived,
                Groups: groupNames,
                DerivedSource: first.IsDerived ? first.DerivedSource : null));
        }

        return new DesiredState(resultTuples, unsupportedList, conflicts);
    }

    private static void AddUnsupported(
        HashSet<(string Exchange, string Canonical)> keys,
        List<UnsupportedTuple> list,
        string exchange, string canonical, string reason)
    {
        if (keys.Add((exchange, canonical)))
            list.Add(new UnsupportedTuple(exchange, canonical, reason));
    }

    private readonly record struct RawTuple(
        string Exchange, string Canonical, VenueInstrument Venue,
        string FeedName, string Interval,
        string Collect, string Format, string HistoryStart,
        bool IsDerived, string Group,
        string? DerivedSource, string? DerivedType, string? DerivedThreshold);
}
