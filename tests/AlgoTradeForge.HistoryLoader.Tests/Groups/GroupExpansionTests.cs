using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Groups;

public sealed class GroupExpansionTests
{
    // --- helpers ---

    private static SymbologyRegistry Registry(params IExchangeSymbology[] symbologies) =>
        new(symbologies);

    private static SymbologyRegistry BinanceRegistry() =>
        Registry(new BinanceSymbology());

    private static CollectionGroup MakeGroup(
        string name = "g1",
        bool enabled = true,
        string[]? exchanges = null,
        string[]? symbols = null,
        string historyStart = "2021-01",
        Dictionary<string, GroupFeed>? feeds = null,
        Dictionary<string, GroupDerived>? derived = null,
        Dictionary<string, IReadOnlyDictionary<string, string>>? symbolOverrides = null) =>
        new(
            Name: name,
            Enabled: enabled,
            Exchanges: exchanges ?? ["binance"],
            Assets: new GroupAssets(
                Symbols: symbols ?? ["BTC/USDT-PERP"],
                HistoryStart: historyStart),
            Feeds: feeds ?? new Dictionary<string, GroupFeed>
            {
                ["candles"] = new GroupFeed("eager", ["1h"], "csv"),
            },
            Derived: derived,
            SymbolOverrides: symbolOverrides);

    // Second exchange for multi-exchange tests — mirrors BinanceSymbology's dir convention.
    private sealed class FakeSymbology(string exchange) : IExchangeSymbology
    {
        public string Exchange => exchange;

        public bool TryResolve(CanonicalSymbol symbol, out VenueInstrument? instrument, out string? unsupportedReason)
        {
            if (symbol.Kind == InstrumentKind.DatedFuture)
            {
                instrument = null;
                unsupportedReason = $"dated futures not supported on {Exchange}";
                return false;
            }
            var apiSymbol = symbol.Base + symbol.Quote;
            var assetType = symbol.Kind == InstrumentKind.Perpetual ? "perpetual" : "spot";
            var dir = symbol.Kind == InstrumentKind.Perpetual ? $"{apiSymbol}_perp" : apiSymbol;
            instrument = new VenueInstrument(apiSymbol, assetType, dir);
            unsupportedReason = null;
            return true;
        }
    }

    // --- (a) 2 exchanges × 2 symbols × candles[1m,1h]+funding → 2·2·3 = 12 tuples ---

    [Fact]
    public void TwoExchanges_TwoSymbols_CandlesAndFunding_ExpandsToTwelve()
    {
        var group = MakeGroup(
            exchanges: ["binance", "okx"],
            symbols: ["BTC/USDT-PERP", "ETH/USDT-PERP"],
            feeds: new Dictionary<string, GroupFeed>
            {
                ["candles"]      = new GroupFeed("eager",     ["1m", "1h"], "csv"),
                ["funding-rate"] = new GroupFeed("on-demand", null,         "csv"),
            });
        var registry = Registry(new BinanceSymbology(), new FakeSymbology("okx"));

        var state = GroupExpansion.Expand([group], registry);

        Assert.Empty(state.Conflicts);
        Assert.Empty(state.Unsupported);
        Assert.Equal(12, state.Tuples.Count);
    }

    // --- (b) overlap: eager beats on-demand + min historyStart + both group names recorded ---

    [Fact]
    public void Merge_EagerBeatsOnDemand_MinHistoryStart_BothGroupNamesRecorded()
    {
        var g1 = MakeGroup("g1", historyStart: "2022-01",
            feeds: new() { ["funding-rate"] = new GroupFeed("on-demand", null, "csv") });
        var g2 = MakeGroup("g2", historyStart: "2021-06",
            feeds: new() { ["funding-rate"] = new GroupFeed("eager", null, "csv") });

        var state = GroupExpansion.Expand([g1, g2], BinanceRegistry());

        Assert.Empty(state.Conflicts);
        Assert.Single(state.Tuples);
        var t = state.Tuples[0];
        Assert.Equal("eager", t.Collect);
        Assert.Equal("2021-06", t.HistoryStart);
        Assert.Equal(new[] { "g1", "g2" }, t.Groups);
    }

    // --- (c) format conflict → GroupConflict(kind=format); conflicting tuple excluded; siblings kept ---

    [Fact]
    public void FormatConflict_ProducesGroupConflict_ExcludesConflictingTuple_KeepsSiblings()
    {
        var g1 = MakeGroup("g1", feeds: new()
        {
            ["candles"]      = new GroupFeed("eager",     ["1h"], "csv"),
            ["funding-rate"] = new GroupFeed("on-demand", null,   "csv"),
        });
        var g2 = MakeGroup("g2", feeds: new()
        {
            ["candles"] = new GroupFeed("eager", ["1h"], "parquet"),
        });

        var state = GroupExpansion.Expand([g1, g2], BinanceRegistry());

        Assert.Single(state.Conflicts);
        Assert.Equal("format", state.Conflicts[0].Kind);
        Assert.Contains("g1", state.Conflicts[0].Groups);
        Assert.Contains("g2", state.Conflicts[0].Groups);
        // sibling funding-rate is not involved in the conflict — it must be kept
        Assert.Single(state.Tuples);
        Assert.Equal("funding-rate", state.Tuples[0].FeedName);
    }

    // --- (d) derived: same-name different-threshold → conflict; identical definition → merged with IsDerived=true ---

    [Fact]
    public void Derived_DifferentThreshold_ProducesConflict()
    {
        var g1 = MakeGroup("g1",
            feeds: new() { ["candles"] = new GroupFeed("eager", ["1m"], "csv") },
            derived: new() { ["EqV_1m_1k"] = new GroupDerived("candles", "EqV", "1000", "1m", "on-demand") });
        var g2 = MakeGroup("g2",
            feeds: new() { ["candles"] = new GroupFeed("eager", ["1m"], "csv") },
            derived: new() { ["EqV_1m_1k"] = new GroupDerived("candles", "EqV", "2000", "1m", "on-demand") });

        var state = GroupExpansion.Expand([g1, g2], BinanceRegistry());

        Assert.Contains(state.Conflicts, c => c.Kind == "derived-definition" && c.Key.Contains("EqV_1m_1k"));
    }

    [Fact]
    public void Derived_IdenticalDefinition_MergedSilentlyWithIsDerivedTrue()
    {
        var derivedDef = new Dictionary<string, GroupDerived>
        {
            ["EqV_1m_1k"] = new GroupDerived("candles", "EqV", "1000", "1m", "on-demand"),
        };
        var g1 = MakeGroup("g1",
            feeds: new() { ["candles"] = new GroupFeed("eager", ["1m"], "csv") },
            derived: derivedDef);
        var g2 = MakeGroup("g2",
            feeds: new() { ["candles"] = new GroupFeed("eager", ["1m"], "csv") },
            derived: derivedDef);

        var state = GroupExpansion.Expand([g1, g2], BinanceRegistry());

        Assert.Empty(state.Conflicts);
        var derivedTuples = state.Tuples.Where(t => t.IsDerived).ToList();
        Assert.Single(derivedTuples);
        Assert.True(derivedTuples[0].IsDerived);
        // collected candles tuple is not marked derived
        Assert.All(state.Tuples.Where(t => !t.IsDerived), t => Assert.False(t.IsDerived));
    }

    [Fact]
    public void Collected_Tuples_AlwaysHaveIsDerived_False()
    {
        var g = MakeGroup(feeds: new()
        {
            ["candles"]      = new GroupFeed("eager", ["1h"], "csv"),
            ["funding-rate"] = new GroupFeed("on-demand", null, "csv"),
        });

        var state = GroupExpansion.Expand([g], BinanceRegistry());

        Assert.All(state.Tuples, t => Assert.False(t.IsDerived));
    }

    // --- (e) FUT symbol → UnsupportedTuple; unknown exchange → unsupported "no symbology for exchange" ---

    [Fact]
    public void FutureSymbol_OnBinance_ProducesUnsupportedTuple()
    {
        var g = MakeGroup(symbols: ["BTC/USDT-FUT-2025-06"]);

        var state = GroupExpansion.Expand([g], BinanceRegistry());

        Assert.Empty(state.Tuples);
        Assert.Single(state.Unsupported);
        Assert.Equal("binance", state.Unsupported[0].Exchange);
        Assert.Contains("BTC/USDT-FUT-2025-06", state.Unsupported[0].Canonical);
    }

    [Fact]
    public void UnknownExchange_ProducesUnsupported_WithNoSymbologyReason()
    {
        var g = MakeGroup(exchanges: ["unknown-exchange"]);

        var state = GroupExpansion.Expand([g], BinanceRegistry());

        Assert.Empty(state.Tuples);
        Assert.Single(state.Unsupported);
        Assert.Equal("unknown-exchange", state.Unsupported[0].Exchange);
        Assert.Contains("no symbology", state.Unsupported[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    // --- (f) disabled group contributes nothing ---

    [Fact]
    public void DisabledGroup_ContributesNothing()
    {
        var g = MakeGroup(enabled: false);

        var state = GroupExpansion.Expand([g], BinanceRegistry());

        Assert.Empty(state.Tuples);
        Assert.Empty(state.Unsupported);
        Assert.Empty(state.Conflicts);
    }

    // --- (g) symbolOverrides swaps ApiSymbol only; dir/type still from symbology ---

    [Fact]
    public void SymbolOverrides_SwapsApiSymbolOnly_DirAndTypeUnchanged()
    {
        var overrides = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["binance"] = new Dictionary<string, string>
            {
                ["BTC/USDT-PERP"] = "BTCUSDTPERP_CUSTOM",
            },
        };
        var g = MakeGroup(symbolOverrides: overrides,
            feeds: new() { ["funding-rate"] = new GroupFeed("eager", null, "csv") });

        var state = GroupExpansion.Expand([g], BinanceRegistry());

        Assert.Single(state.Tuples);
        var t = state.Tuples[0];
        Assert.Equal("BTCUSDTPERP_CUSTOM", t.Venue!.ApiSymbol);
        Assert.Equal("perpetual", t.Venue.AssetType);
        Assert.NotNull(t.Venue.Dir);          // dir still from symbology, not the override
    }

    // --- (h) "BINANCE" and "binance" for same symbol+feed → ONE merged tuple with Exchange=="binance" ---

    [Fact]
    public void CaseNormalization_UpperAndLowerExchange_ProduceSingleMergedTuple()
    {
        var g1 = MakeGroup("g1", exchanges: ["BINANCE"],
            feeds: new() { ["funding-rate"] = new GroupFeed("on-demand", null, "csv") });
        var g2 = MakeGroup("g2", exchanges: ["binance"],
            feeds: new() { ["funding-rate"] = new GroupFeed("on-demand", null, "csv") });

        var state = GroupExpansion.Expand([g1, g2], BinanceRegistry());

        Assert.Empty(state.Conflicts);
        Assert.Single(state.Tuples);
        Assert.Equal("binance", state.Tuples[0].Exchange);
    }

    // --- (i) same FUT symbol unsupported in two groups → ONE UnsupportedTuple ---

    [Fact]
    public void FutureSymbol_InTwoGroups_ProducesOneUnsupportedTuple()
    {
        var g1 = MakeGroup("g1", symbols: ["BTC/USDT-FUT-2025-06"]);
        var g2 = MakeGroup("g2", symbols: ["BTC/USDT-FUT-2025-06"]);

        var state = GroupExpansion.Expand([g1, g2], BinanceRegistry());

        Assert.Single(state.Unsupported);
        Assert.Equal("binance", state.Unsupported[0].Exchange);
    }
}
