using AlgoTradeForge.HistoryLoader.Application.Groups;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Groups;

public sealed class GroupValidatorTests
{
    private static CollectionGroup ValidGroup() => new(
        Name: "btc-perp-binance",
        Enabled: true,
        Exchanges: ["binance"],
        Assets: new GroupAssets(
            Symbols: ["BTC/USDT-PERP"],
            HistoryStart: "2021-01"),
        Feeds: new Dictionary<string, GroupFeed>
        {
            ["candles"]      = new GroupFeed("eager", ["1h", "4h"], null),
            ["funding-rate"] = new GroupFeed("on-demand", null, null),
        },
        Derived: null,
        SymbolOverrides: null);

    [Fact]
    public void ValidGroup_ReturnsNoErrors()
    {
        var errors = GroupValidator.Validate(ValidGroup());
        Assert.Empty(errors);
    }

    // --- Name ---

    [Theory]
    [InlineData("Invalid Name!")]   // spaces + uppercase
    [InlineData("_foo")]            // starts with underscore
    [InlineData("")]                // empty
    [InlineData("A")]               // uppercase single char
    public void Name_InvalidPattern_ReturnsError(string name)
    {
        var group = ValidGroup() with { Name = name };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("name"));
    }

    // --- Exchanges ---

    [Fact]
    public void Exchanges_Empty_ReturnsError()
    {
        var group = ValidGroup() with { Exchanges = [] };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("exchanges"));
    }

    [Fact]
    public void Exchange_Uppercase_ReturnsErrorNamingOffender()
    {
        var group = ValidGroup() with { Exchanges = ["Binance"] };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("Binance") && e.Contains("lowercase"));
    }

    // --- Symbols ---

    [Fact]
    public void Symbols_Empty_ReturnsError()
    {
        var group = ValidGroup() with { Assets = ValidGroup().Assets with { Symbols = [] } };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("symbols"));
    }

    [Fact]
    public void Symbol_InvalidCanonical_ReturnsErrorCarryingSymbol()
    {
        var group = ValidGroup() with { Assets = ValidGroup().Assets with { Symbols = ["BTCUSDT"] } };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("BTCUSDT"));
    }

    // --- HistoryStart ---

    [Theory]
    [InlineData("2021/01")]   // wrong separator
    [InlineData("bad")]       // garbage
    [InlineData("21-01")]     // short year
    public void HistoryStart_InvalidFormat_ReturnsError(string historyStart)
    {
        var group = ValidGroup() with { Assets = ValidGroup().Assets with { HistoryStart = historyStart } };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("historyStart"));
    }

    [Fact]
    public void HistoryStart_ValidYearMonth_NoError()
    {
        var group = ValidGroup() with { Assets = ValidGroup().Assets with { HistoryStart = "2023-11" } };
        var errors = GroupValidator.Validate(group);
        Assert.Empty(errors);
    }

    // --- Feed key allow-list ---

    [Fact]
    public void FeedKey_CandleExt_ReturnsExplicitSideOutputError()
    {
        var feeds = new Dictionary<string, GroupFeed>(ValidGroup().Feeds)
        {
            ["candle-ext"] = new GroupFeed("eager", null, null)
        };
        var group = ValidGroup() with { Feeds = feeds };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("candle-ext") && e.Contains("side-output"));
    }

    [Fact]
    public void FeedKey_Session_ReturnsExplicitInternalMarkerError()
    {
        var feeds = new Dictionary<string, GroupFeed>(ValidGroup().Feeds)
        {
            ["_session"] = new GroupFeed("eager", null, null)
        };
        var group = ValidGroup() with { Feeds = feeds };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("_session") && e.Contains("internal marker"));
    }

    [Fact]
    public void FeedKey_Unknown_ReturnsGenericErrorNamingKey()
    {
        var feeds = new Dictionary<string, GroupFeed>(ValidGroup().Feeds)
        {
            ["mystery-feed"] = new GroupFeed("eager", null, null)
        };
        var group = ValidGroup() with { Feeds = feeds };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("mystery-feed"));
        Assert.DoesNotContain(errors, e => e.Contains("side-output"));
        Assert.DoesNotContain(errors, e => e.Contains("internal marker"));
    }

    // --- Candles intervals ---

    [Fact]
    public void Candles_NullIntervals_ReturnsError()
    {
        var feeds = new Dictionary<string, GroupFeed>
        {
            ["candles"] = new GroupFeed("eager", null, null)
        };
        var group = ValidGroup() with { Feeds = feeds };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("candles") && e.Contains("intervals"));
    }

    [Fact]
    public void Candles_EmptyIntervals_ReturnsError()
    {
        var feeds = new Dictionary<string, GroupFeed>
        {
            ["candles"] = new GroupFeed("eager", [], null)
        };
        var group = ValidGroup() with { Feeds = feeds };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("candles") && e.Contains("intervals"));
    }

    [Fact]
    public void NonCandle_WithNonEmptyIntervals_ReturnsError()
    {
        var feeds = new Dictionary<string, GroupFeed>
        {
            ["candles"]      = new GroupFeed("eager", ["1h"], null),
            ["funding-rate"] = new GroupFeed("eager", ["8h"], null)
        };
        var group = ValidGroup() with { Feeds = feeds };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("funding-rate") && e.Contains("intervals"));
    }

    // --- Enum domains ---

    [Fact]
    public void Feed_InvalidCollect_ReturnsError()
    {
        var feeds = new Dictionary<string, GroupFeed>
        {
            ["candles"] = new GroupFeed("lazy", ["1h"], null)
        };
        var group = ValidGroup() with { Feeds = feeds };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("collect") && e.Contains("lazy"));
    }

    [Fact]
    public void Feed_InvalidFormat_ReturnsError()
    {
        var feeds = new Dictionary<string, GroupFeed>
        {
            ["candles"] = new GroupFeed("eager", ["1h"], "json")
        };
        var group = ValidGroup() with { Feeds = feeds };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("format") && e.Contains("json"));
    }

    [Fact]
    public void Derived_InvalidMaterialize_ReturnsError()
    {
        var derived = new Dictionary<string, GroupDerived>
        {
            ["renko"] = new GroupDerived("candles", null, null, "1h", "weekly")
        };
        var group = ValidGroup() with { Derived = derived };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("materialize") && e.Contains("weekly"));
    }

    // --- Derived name collision with declarable feeds ---

    [Fact]
    public void Derived_NameCollidingWithDeclarableFeed_IsError()
    {
        var group = ValidGroup() with { Derived = new Dictionary<string, GroupDerived>
            { ["mark-price"] = new GroupDerived("candles", "EqV", null, null, "on-demand") } };
        Assert.Contains(GroupValidator.Validate(group),
            e => e.Contains("derived 'mark-price'") && e.Contains("collides"));
    }

    // --- Derived source ---

    [Fact]
    public void Derived_SourceNotInFeeds_ReturnsError()
    {
        // "ticks" is not in ValidGroup().Feeds
        var derived = new Dictionary<string, GroupDerived>
        {
            ["renko"] = new GroupDerived("ticks", null, null, null, "on-demand")
        };
        var group = ValidGroup() with { Derived = derived };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("renko") && e.Contains("ticks"));
    }

    [Fact]
    public void Derived_SourceCandles_IsAlwaysValid()
    {
        // "candles" is always a valid derived source even when feeds only contains funding-rate.
        // The derived key must be a canonical AltBar id (Type_Source_Threshold).
        var feeds = new Dictionary<string, GroupFeed>
        {
            ["funding-rate"] = new GroupFeed("eager", null, null)
        };
        var derived = new Dictionary<string, GroupDerived>
        {
            ["Renko_1h_500"] = new GroupDerived("candles", null, null, "1h", "eager")
        };
        var group = ValidGroup() with { Feeds = feeds, Derived = derived };
        var errors = GroupValidator.Validate(group);
        Assert.DoesNotContain(errors, e => e.Contains("Renko_1h_500"));
    }

    [Fact]
    public void Derived_SourceInDeclaredFeeds_IsValid()
    {
        var feeds = new Dictionary<string, GroupFeed>
        {
            ["candles"] = new GroupFeed("eager", ["1h"], null),
            ["ticks"]   = new GroupFeed("eager", null, null),
        };
        var derived = new Dictionary<string, GroupDerived>
        {
            ["EqV_ticks_1k"] = new GroupDerived("ticks", null, null, null, "eager")
        };
        var group = ValidGroup() with { Feeds = feeds, Derived = derived };
        var errors = GroupValidator.Validate(group);
        Assert.DoesNotContain(errors, e => e.Contains("EqV_ticks_1k"));
    }

    // --- Derived key canonicality (Type_Source_Threshold) ---

    [Fact]
    public void Derived_NonCanonicalKey_IsConfigTimeError()
    {
        // A 2-component id (EqV_1k) is NOT a canonical AltBarFeedId. Without this gate the
        // materialize worker would parse it at runtime and throw FormatException → materialize_failed.
        var feeds = new Dictionary<string, GroupFeed>
        {
            ["candles"] = new GroupFeed("eager", ["1h"], null),
            ["ticks"]   = new GroupFeed("eager", null, null),
        };
        var derived = new Dictionary<string, GroupDerived>
        {
            ["EqV_1k"] = new GroupDerived("ticks", null, null, null, "eager")
        };
        var group = ValidGroup() with { Feeds = feeds, Derived = derived };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("derived_feed_not_canonical") && e.Contains("EqV_1k"));
    }

    [Fact]
    public void Derived_CanonicalKey_Passes()
    {
        var feeds = new Dictionary<string, GroupFeed>
        {
            ["candles"] = new GroupFeed("eager", ["1h"], null),
            ["ticks"]   = new GroupFeed("eager", null, null),
        };
        var derived = new Dictionary<string, GroupDerived>
        {
            ["EqV_ticks_1k"] = new GroupDerived("ticks", null, null, null, "eager")
        };
        var group = ValidGroup() with { Feeds = feeds, Derived = derived };
        var errors = GroupValidator.Validate(group);
        Assert.DoesNotContain(errors, e => e.Contains("derived_feed_not_canonical"));
    }

    // --- SymbolOverrides ---

    [Fact]
    public void SymbolOverrides_ExchangeNotInExchanges_ReturnsError()
    {
        var overrides = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["kraken"] = new Dictionary<string, string> { ["BTC/USDT-PERP"] = "BTC/USDT-PERP" }
        };
        var group = ValidGroup() with { SymbolOverrides = overrides };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("kraken"));
    }

    [Fact]
    public void SymbolOverrides_InvalidOverrideKey_ReturnsError()
    {
        var overrides = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["binance"] = new Dictionary<string, string> { ["BTCUSDT"] = "BTCPERP" }
        };
        var group = ValidGroup() with { SymbolOverrides = overrides };
        var errors = GroupValidator.Validate(group);
        Assert.Contains(errors, e => e.Contains("BTCUSDT"));
    }

    [Fact]
    public void SymbolOverrides_ValidExchangeAndKey_NoError()
    {
        var overrides = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["binance"] = new Dictionary<string, string> { ["BTC/USDT-PERP"] = "BTCUSDT" }
        };
        var group = ValidGroup() with { SymbolOverrides = overrides };
        var errors = GroupValidator.Validate(group);
        Assert.Empty(errors);
    }

    // --- Multi-error ---

    [Fact]
    public void MultipleViolations_AllErrorsReturned()
    {
        var group = new CollectionGroup(
            Name: "INVALID NAME",            // name fails
            Enabled: true,
            Exchanges: ["Binance"],          // uppercase exchange
            Assets: new GroupAssets(
                Symbols: ["BTCUSDT"],        // no '/'
                HistoryStart: "bad"),        // bad format
            Feeds: new Dictionary<string, GroupFeed>
            {
                ["candles"] = new GroupFeed("lazy", null, null)  // bad collect + no intervals
            },
            Derived: null,
            SymbolOverrides: null);

        var errors = GroupValidator.Validate(group);

        // Expect at least: name + exchange + symbol + historyStart + collect + intervals = 6
        Assert.True(errors.Count >= 4,
            $"Expected >=4 errors, got {errors.Count}: {string.Join("; ", errors)}");
        Assert.Contains(errors, e => e.Contains("name"));
        Assert.Contains(errors, e => e.Contains("Binance") && e.Contains("lowercase"));
        Assert.Contains(errors, e => e.Contains("BTCUSDT"));
        Assert.Contains(errors, e => e.Contains("historyStart"));
    }
}
