using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Groups;
using AlgoTradeForge.HistoryLoader.Domain;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Groups;

public sealed class GroupCollectabilityValidatorTests
{
    private static IArchiveMaterializer Stub(string exchange, string feed, bool futuresOnly = false)
    {
        var m = Substitute.For<IArchiveMaterializer>();
        m.Exchange.Returns(exchange);
        m.FeedName.Returns(feed);
        m.Supports(Arg.Any<string>()).Returns(ci => !futuresOnly || AssetTypes.IsFutures(ci.Arg<string>()));
        return m;
    }

    private static ArchiveMaterializerRegistry Registry(params IArchiveMaterializer[] materializers) =>
        new(materializers);

    private static CollectionGroup Group(
        IReadOnlyList<string> symbols, params (string Feed, string Collect)[] feeds) => new(
        Name: "g",
        Enabled: true,
        Exchanges: ["binance"],
        Assets: new GroupAssets(symbols, "2023-01"),
        Feeds: feeds.ToDictionary(
            f => f.Feed,
            f => new GroupFeed(f.Collect, f.Feed == FeedNames.Candles ? ["1h"] : null, null)),
        Derived: null,
        SymbolOverrides: null);

    // --- The bug: on-demand + not replenishable anywhere = never collected ---

    [Fact]
    public void Liquidations_OnDemand_Perp_NoMaterializer_IsError()
    {
        var group = Group(["BTC/USDT-PERP"], (FeedNames.Liquidations, "on-demand"));
        var errors = GroupCollectabilityValidator.Validate(group, Registry());
        Assert.Contains(errors, e => e.Contains(FeedNames.Liquidations) && e.Contains("on-demand"));
    }

    [Fact]
    public void BookTicker_OnDemand_Spot_NoMaterializer_IsError()
    {
        var group = Group(["BTC/USDT"], (FeedNames.BookTicker, "on-demand"));
        var errors = GroupCollectabilityValidator.Validate(group, Registry());
        Assert.Contains(errors, e => e.Contains(FeedNames.BookTicker));
    }

    // --- No false positives ---

    [Fact]
    public void FundingRate_OnDemand_MixedGroup_ReplenishableForPerp_NoError()
    {
        // funding-rate is futures-only replenishable; a group mixing spot + perp is fine on-demand
        // because the perp assets have an archive path (spot funding-rate simply isn't collected).
        var group = Group(["BTC/USDT", "ETH/USDT-PERP"], (FeedNames.FundingRate, "on-demand"));
        var registry = Registry(Stub("binance", FeedNames.FundingRate, futuresOnly: true));
        Assert.Empty(GroupCollectabilityValidator.Validate(group, registry));
    }

    [Fact]
    public void Ticks_OnDemand_Spot_WithMaterializer_NoError()
    {
        var group = Group(["BTC/USDT"], (FeedNames.Ticks, "on-demand"));
        var registry = Registry(Stub("binance", FeedNames.Ticks));
        Assert.Empty(GroupCollectabilityValidator.Validate(group, registry));
    }

    [Fact]
    public void NonReplenishableFeed_DeclaredEager_IsNotFlagged()
    {
        // eager liquidations is exactly how a stream-only feed should be declared — never an error.
        var group = Group(["BTC/USDT-PERP"], (FeedNames.Liquidations, "eager"));
        Assert.Empty(GroupCollectabilityValidator.Validate(group, Registry()));
    }

    [Fact]
    public void Candles_OnDemand_WithMaterializer_NoError()
    {
        var group = Group(["BTC/USDT"], (FeedNames.Candles, "on-demand"));
        var registry = Registry(Stub("binance", FeedNames.Candles));
        Assert.Empty(GroupCollectabilityValidator.Validate(group, registry));
    }

    [Fact]
    public void UnparseableSymbols_DoesNotThrow_DefersToStructuralValidator()
    {
        // structural GroupValidator reports the bad symbol; this validator must not throw or
        // spuriously flag when it cannot derive any asset type.
        var group = Group(["NOTVALID"], (FeedNames.Liquidations, "on-demand"));
        Assert.Empty(GroupCollectabilityValidator.Validate(group, Registry()));
    }
}
