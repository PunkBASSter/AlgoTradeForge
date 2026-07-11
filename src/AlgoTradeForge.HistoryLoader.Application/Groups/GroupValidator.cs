using System.Collections.Frozen;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

// Feed names a group file may declare; side-outputs and internal markers are excluded. See FeedNames.
internal static class DeclarableFeeds
{
    internal static readonly FrozenSet<string> All = FrozenSet.Create(
        FeedNames.Candles,
        FeedNames.FundingRate,
        FeedNames.MarkPrice,
        FeedNames.PremiumIndex,
        FeedNames.IndexPrice,
        FeedNames.OpenInterest,
        FeedNames.TakerVolume,
        FeedNames.LsRatioGlobal,
        FeedNames.LsRatioTopAccounts,
        FeedNames.LsRatioTopPositions,
        FeedNames.Liquidations,
        FeedNames.Ticks,
        FeedNames.BookTicker);
}

public static class GroupValidator
{
    /// <summary>Structural validation of ONE group: name regex ^[a-z0-9][a-z0-9_-]{0,63}$ (and name
    /// must equal the file name, enforced by the store); non-empty exchanges/symbols; every exchange
    /// entry must be lowercase (error names the offender — canonical exchange ids are lowercase,
    /// see IExchangeSymbology.Exchange); every symbol parses canonically (errors carry the symbol);
    /// historyStart is yyyy-MM; feed keys ∈ the DeclarableFeeds allow-list ("candles" requires
    /// non-empty Intervals, others must not set Intervals); enum domains explicit: collect ∈ {eager, on-demand}, materialize ∈ {eager, on-demand},
    /// format ∈ {csv, parquet}; derived.source references a key in feeds or "candles";
    /// symbolOverrides keys ⊆ exchanges, override keys parse canonically.
    /// Returns all errors, not first-only.</summary>
    public static IReadOnlyList<string> Validate(CollectionGroup group)
    {
        var errors = new List<string>();

        if (!GroupName.IsValid(group.Name))
            errors.Add($"name '{group.Name}' does not match ^[a-z0-9][a-z0-9_-]{{0,63}}$");

        if (group.Exchanges is null or { Count: 0 })
        {
            errors.Add("exchanges must not be empty");
        }
        else
        {
            foreach (var exchange in group.Exchanges)
            {
                if (exchange != exchange.ToLowerInvariant())
                    errors.Add($"exchange '{exchange}' must be lowercase");
            }
        }

        if (group.Assets.Symbols is null or { Count: 0 })
        {
            errors.Add("assets.symbols must not be empty");
        }
        else
        {
            foreach (var symbol in group.Assets.Symbols)
            {
                if (!CanonicalSymbolParser.TryParse(symbol, out _, out var symbolErr))
                    errors.Add($"symbol '{symbol}': {symbolErr}");
            }
        }

        if (!IsValidYearMonth(group.Assets.HistoryStart))
            errors.Add($"assets.historyStart '{group.Assets.HistoryStart}' must be yyyy-MM");

        ValidateFeeds(group.Feeds, errors);

        if (group.Derived is not null)
            ValidateDerived(group.Derived, group.Feeds, errors);

        if (group.SymbolOverrides is not null)
            ValidateSymbolOverrides(group.SymbolOverrides, group.Exchanges, errors);

        return errors;
    }

    private static void ValidateFeeds(
        IReadOnlyDictionary<string, GroupFeed> feeds,
        List<string> errors)
    {
        foreach (var (feedKey, feedDef) in feeds)
        {
            if (feedKey == FeedNames.CandleExt)
            {
                errors.Add(
                    "candle-ext is a side-output written alongside candles, declare candles intervals instead");
                continue;
            }
            if (feedKey == FeedNames.Session)
            {
                errors.Add($"'{FeedNames.Session}' is an internal marker, not a collectable feed");
                continue;
            }
            if (!DeclarableFeeds.All.Contains(feedKey))
            {
                errors.Add($"unknown feed '{feedKey}'");
                continue;
            }

            if (feedKey == FeedNames.Candles)
            {
                if (feedDef.Intervals is null or { Count: 0 })
                    errors.Add("candles feed requires non-empty intervals");
            }
            else
            {
                if (feedDef.Intervals is { Count: > 0 })
                    errors.Add($"feed '{feedKey}' must not set intervals");
            }

            if (feedDef.Collect is not ("eager" or "on-demand"))
                errors.Add($"feed '{feedKey}': collect must be eager or on-demand, got '{feedDef.Collect}'");

            if (feedDef.Format is not null and not ("csv" or "parquet"))
                errors.Add($"feed '{feedKey}': format must be csv or parquet, got '{feedDef.Format}'");
        }
    }

    private static void ValidateDerived(
        IReadOnlyDictionary<string, GroupDerived> derived,
        IReadOnlyDictionary<string, GroupFeed> feeds,
        List<string> errors)
    {
        // "candles" is always a valid source (brief: "references a key in feeds or 'candles'")
        var validSources = new HashSet<string>(feeds.Keys) { FeedNames.Candles };

        foreach (var (derivedKey, derivedDef) in derived)
        {
            if (!validSources.Contains(derivedDef.Source))
                errors.Add(
                    $"derived '{derivedKey}': source '{derivedDef.Source}' is not a declared feed");

            if (derivedDef.Materialize is not ("eager" or "on-demand"))
                errors.Add(
                    $"derived '{derivedKey}': materialize must be eager or on-demand, got '{derivedDef.Materialize}'");
        }
    }

    private static void ValidateSymbolOverrides(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> symbolOverrides,
        IReadOnlyList<string>? exchanges,
        List<string> errors)
    {
        var exchangeSet = exchanges is null
            ? new HashSet<string>()
            : new HashSet<string>(exchanges);

        foreach (var (overrideExchange, overrides) in symbolOverrides)
        {
            if (!exchangeSet.Contains(overrideExchange))
                errors.Add($"symbolOverrides key '{overrideExchange}' is not in exchanges");

            if (overrides is null) continue;

            foreach (var overrideKey in overrides.Keys)
            {
                if (!CanonicalSymbolParser.TryParse(overrideKey, out _, out var overrideErr))
                    errors.Add(
                        $"symbolOverrides['{overrideExchange}'] key '{overrideKey}': {overrideErr}");
            }
        }
    }

    // Validates yyyy-MM in range 2000-01..2099-12.
    private static bool IsValidYearMonth(string? value)
    {
        if (value is null || value.Length != 7 || value[4] != '-')
            return false;
        if (!int.TryParse(value[..4], out int year) || !int.TryParse(value[5..], out int month))
            return false;
        return year is >= 2000 and <= 2099 && month is >= 1 and <= 12;
    }
}
