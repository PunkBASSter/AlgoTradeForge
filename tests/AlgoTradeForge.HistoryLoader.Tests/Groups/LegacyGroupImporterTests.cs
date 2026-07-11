using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Domain;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Groups;

public sealed class LegacyGroupImporterTests
{
    // --- helpers ---

    private static ArchiveMaterializerRegistry EmptyRegistry() => new([]);

    private static ArchiveMaterializerRegistry ReplenishableFor(string feedName)
    {
        var m = Substitute.For<IArchiveMaterializer>();
        m.Exchange.Returns("binance");
        m.FeedName.Returns(feedName);
        m.Supports(Arg.Any<string>()).Returns(true);
        return new ArchiveMaterializerRegistry([m]);
    }

    private static AssetCollectionConfig MakeAsset(
        string symbol,
        string type = AssetTypes.Spot,
        string exchange = "binance",
        DateOnly? historyStart = null,
        List<FeedCollectionConfig>? feeds = null) =>
        new()
        {
            Symbol = symbol,
            Type = type,
            Exchange = exchange,
            HistoryStart = historyStart ?? new DateOnly(2021, 1, 1),
            Feeds = feeds ?? [],
        };

    private static HistoryLoaderOptions OptionsFrom(params AssetCollectionConfig[] assets) =>
        new() { Assets = [..assets] };

    // --- (a) mixed spot+perp → two groups ---

    [Fact]
    public void MixedSpotPerp_TwoGroups_OneSpotOnePerp()
    {
        var opts = OptionsFrom(
            MakeAsset("BTCUSDT", AssetTypes.Spot),
            MakeAsset("ETHUSDT", AssetTypes.Perpetual));

        var (groups, _) = LegacyGroupImporter.Convert(opts, EmptyRegistry());

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Name == "legacy-binance-spot");
        Assert.Contains(groups, g => g.Name == "legacy-binance-perp");
    }

    // --- (b) Eager flag honored ---

    [Fact]
    public void EagerFeed_CollectIsEager_EvenIfReplenishable()
    {
        // Registry has funding-rate as replenishable — without Eager it would be on-demand.
        var registry = ReplenishableFor(FeedNames.FundingRate);
        var feeds = new List<FeedCollectionConfig>
        {
            new() { Name = FeedNames.FundingRate, Enabled = true, Eager = true },
        };
        var opts = OptionsFrom(MakeAsset("BTCUSDT", AssetTypes.Perpetual, feeds: feeds));

        var (groups, _) = LegacyGroupImporter.Convert(opts, registry);

        Assert.Single(groups);
        Assert.Equal("eager", groups[0].Feeds[FeedNames.FundingRate].Collect);
    }

    // --- (c) ticks lazy via replenishable registry ---

    [Fact]
    public void TicksFeed_Replenishable_CollectIsOnDemand()
    {
        var registry = ReplenishableFor(FeedNames.Ticks);
        var feeds = new List<FeedCollectionConfig>
        {
            new() { Name = FeedNames.Ticks, Enabled = true, Eager = false },
        };
        var opts = OptionsFrom(MakeAsset("BTCUSDT", feeds: feeds));

        var (groups, _) = LegacyGroupImporter.Convert(opts, registry);

        Assert.Single(groups);
        Assert.Equal("on-demand", groups[0].Feeds[FeedNames.Ticks].Collect);
    }

    [Fact]
    public void TicksFeed_NotReplenishable_CollectIsEager()
    {
        var feeds = new List<FeedCollectionConfig>
        {
            new() { Name = FeedNames.Ticks, Enabled = true, Eager = false },
        };
        var opts = OptionsFrom(MakeAsset("BTCUSDT", feeds: feeds));

        var (groups, _) = LegacyGroupImporter.Convert(opts, EmptyRegistry());

        Assert.Single(groups);
        Assert.Equal("eager", groups[0].Feeds[FeedNames.Ticks].Collect);
    }

    // --- (d) Enabled==false feed omitted ---

    [Fact]
    public void DisabledFeed_NotIncludedInGroup()
    {
        var feeds = new List<FeedCollectionConfig>
        {
            new() { Name = FeedNames.FundingRate, Enabled = true },
            new() { Name = FeedNames.MarkPrice, Enabled = false },
        };
        var opts = OptionsFrom(MakeAsset("BTCUSDT", AssetTypes.Perpetual, feeds: feeds));

        var (groups, _) = LegacyGroupImporter.Convert(opts, EmptyRegistry());

        Assert.Single(groups);
        Assert.True(groups[0].Feeds.ContainsKey(FeedNames.FundingRate));
        Assert.False(groups[0].Feeds.ContainsKey(FeedNames.MarkPrice));
    }

    // --- (e) XXXBUSD → XXX/BUSD; BUSD wins over absent USD ---

    [Fact]
    public void BusdSymbol_MapsToXxxSlashBusd()
    {
        var opts = OptionsFrom(MakeAsset("ETHBUSD"));

        var (groups, warnings) = LegacyGroupImporter.Convert(opts, EmptyRegistry());

        Assert.Single(groups);
        Assert.Empty(warnings);
        Assert.Contains("ETH/BUSD", groups[0].Assets.Symbols);
    }

    // --- (f) bare USD suffix → warning, no crash ---

    [Fact]
    public void BareUsdSuffix_ProducesWarning_NoCrash()
    {
        var opts = OptionsFrom(MakeAsset("BTCUSD")); // USD is absent from the suffix list

        var (groups, warnings) = LegacyGroupImporter.Convert(opts, EmptyRegistry());

        Assert.NotEmpty(warnings);
        Assert.Empty(groups);
    }

    // --- (g) unmappable symbol (no recognized suffix) → warning, no crash ---

    [Fact]
    public void UnmappableSymbol_ProducesWarning_NoCrash()
    {
        var opts = OptionsFrom(MakeAsset("BTCEUR")); // EUR not in suffix list

        var (groups, warnings) = LegacyGroupImporter.Convert(opts, EmptyRegistry());

        Assert.NotEmpty(warnings);
        Assert.Empty(groups);
    }

    // --- (h) invalid char in base token → warning, valid siblings still imported ---

    [Fact]
    public void InvalidCharInBaseToken_SkippedWithWarning_ValidSiblingsImported()
    {
        var opts = OptionsFrom(
            MakeAsset("BTC-USDT"), // '-' invalid in base token; would fail GroupValidator at Put
            MakeAsset("ETHUSDT"));

        var (groups, warnings) = LegacyGroupImporter.Convert(opts, EmptyRegistry());

        Assert.Contains(warnings, w => w.Contains("BTC-USDT"));
        Assert.Single(groups);
        Assert.Equal(["ETH/USDT"], groups[0].Assets.Symbols);
    }

    // --- additional coverage ---

    [Fact]
    public void PerpSymbol_MapsWithPerpSuffix()
    {
        var opts = OptionsFrom(MakeAsset("BTCUSDT", AssetTypes.Perpetual,
            historyStart: new DateOnly(2022, 3, 1)));

        var (groups, _) = LegacyGroupImporter.Convert(opts, EmptyRegistry());

        Assert.Single(groups);
        Assert.Contains("BTC/USDT-PERP", groups[0].Assets.Symbols);
        Assert.Equal("2022-03", groups[0].Assets.HistoryStart);
    }

    [Fact]
    public void HistoryStart_IsMinAcrossAssets()
    {
        var opts = OptionsFrom(
            MakeAsset("BTCUSDT", historyStart: new DateOnly(2022, 1, 1)),
            MakeAsset("ETHUSDT", historyStart: new DateOnly(2021, 6, 1)));

        var (groups, _) = LegacyGroupImporter.Convert(opts, EmptyRegistry());

        Assert.Single(groups);
        Assert.Equal("2021-06", groups[0].Assets.HistoryStart);
    }

    [Fact]
    public void CandleIntervals_DedupedAndSorted_AcrossAssets()
    {
        var asset1 = MakeAsset("BTCUSDT", feeds: [
            new() { Name = FeedNames.Candles, Interval = "1h", Enabled = true },
        ]);
        var asset2 = MakeAsset("ETHUSDT", feeds: [
            new() { Name = FeedNames.Candles, Interval = "1h", Enabled = true },
            new() { Name = FeedNames.Candles, Interval = "1m", Enabled = true },
        ]);
        var opts = OptionsFrom(asset1, asset2);

        var (groups, _) = LegacyGroupImporter.Convert(opts, EmptyRegistry());

        Assert.Single(groups);
        var intervals = groups[0].Feeds[FeedNames.Candles].Intervals;
        Assert.NotNull(intervals);
        Assert.Equal(2, intervals!.Count); // 1h and 1m, deduplicated
    }
}
