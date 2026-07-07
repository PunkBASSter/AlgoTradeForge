using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class LoadEndpointValidationTests
{
    private static readonly LoadOptions DefaultOptions = new() { MaxMonthsPerRequest = 600 };

    private static ArchiveMaterializerRegistry RegistryWithCandles()
    {
        var archive = Substitute.For<IBinanceArchiveClient>();
        var pw = Substitute.For<IPartitionFileWriter>();
        var sm = Substitute.For<ISchemaManager>();
        var fs = Substitute.For<IFeedStatusStore>();
        return new ArchiveMaterializerRegistry(
        [
            new KlinesArchiveMaterializer(
                FeedNames.Candles, "klines", supportsSpot: true,
                archive, pw, sm, fs, NullLogger<KlinesArchiveMaterializer>.Instance),
        ]);
    }

    private static LoadRequest ValidRequest() => new(
        Exchange: "binance",
        Symbol: "BTCUSDT",
        AssetType: AssetTypes.Spot,
        FeedName: FeedNames.Candles,
        Interval: "1h",
        From: new DateOnly(2024, 1, 1),
        To: new DateOnly(2024, 3, 31));

    [Fact]
    public void UnknownAssetType_Returns_UnknownAssetType()
    {
        var req = ValidRequest() with { AssetType = "alien" };
        var err = LoadRequestValidator.Validate(req, RegistryWithCandles(), DefaultOptions);
        Assert.NotNull(err);
        Assert.Equal("unknown_asset_type", err!.Code);
    }

    [Fact]
    public void FromAfterTo_Returns_InvalidRange()
    {
        var req = ValidRequest() with
        {
            From = new DateOnly(2024, 6, 1),
            To = new DateOnly(2024, 1, 1),
        };
        var err = LoadRequestValidator.Validate(req, RegistryWithCandles(), DefaultOptions);
        Assert.NotNull(err);
        Assert.Equal("invalid_range", err!.Code);
    }

    [Fact]
    public void TooManyMonths_Returns_TooManyMonths()
    {
        // MaxMonthsPerRequest = 10; request spans 12 months
        var opts = new LoadOptions { MaxMonthsPerRequest = 10 };
        var req = ValidRequest() with
        {
            From = new DateOnly(2024, 1, 1),
            To = new DateOnly(2024, 12, 31),
        };
        var err = LoadRequestValidator.Validate(req, RegistryWithCandles(), opts);
        Assert.NotNull(err);
        Assert.Equal("too_many_months", err!.Code);
    }

    [Fact]
    public void NotReplenishable_Returns_NotReplenishable()
    {
        var req = ValidRequest() with { FeedName = FeedNames.Liquidations };
        var err = LoadRequestValidator.Validate(req, RegistryWithCandles(), DefaultOptions);
        Assert.NotNull(err);
        Assert.Equal("not_replenishable", err!.Code);
    }

    [Fact]
    public void HappyPath_ReturnsNull()
    {
        var err = LoadRequestValidator.Validate(ValidRequest(), RegistryWithCandles(), DefaultOptions);
        Assert.Null(err);
    }
}
