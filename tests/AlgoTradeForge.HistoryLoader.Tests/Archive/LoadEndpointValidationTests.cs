using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    [Theory]
    [InlineData("bogus")]
    [InlineData("7x")]
    [InlineData("")]
    public void GarbageInterval_Returns_InvalidInterval(string interval)
    {
        var req = ValidRequest() with { Interval = interval };
        var err = LoadRequestValidator.Validate(req, RegistryWithCandles(), DefaultOptions);
        Assert.NotNull(err);
        Assert.Equal("invalid_interval", err!.Code);
    }

    [Theory]
    [InlineData("5m")]
    [InlineData("1h")]
    public void ValidIntervals_Pass(string interval)
    {
        var req = ValidRequest() with { Interval = interval };
        var err = LoadRequestValidator.Validate(req, RegistryWithCandles(), DefaultOptions);
        Assert.Null(err);
    }

    // -------------------------------------------------------------------------
    // Task 9: Tick disk-budget guard + interval-less validator bypass
    // -------------------------------------------------------------------------

    private static ArchiveMaterializerRegistry RegistryWithTicks()
    {
        var archive = Substitute.For<IBinanceArchiveClient>();
        var pw = Substitute.For<IPartitionFileWriter>();
        var sm = Substitute.For<ISchemaManager>();
        var fs = Substitute.For<IFeedStatusStore>();
        return new ArchiveMaterializerRegistry(
        [
            new AggTradesArchiveMaterializer(
                archive, pw, sm, fs, NullLogger<AggTradesArchiveMaterializer>.Instance),
        ]);
    }

    private static ArchiveMaterializerRegistry RegistryWithFunding()
    {
        var archive = Substitute.For<IBinanceArchiveClient>();
        var pw = Substitute.For<IPartitionFileWriter>();
        var sm = Substitute.For<ISchemaManager>();
        var fs = Substitute.For<IFeedStatusStore>();
        return new ArchiveMaterializerRegistry(
        [
            new FundingRateArchiveMaterializer(
                archive, pw, sm, fs, NullLogger<FundingRateArchiveMaterializer>.Instance),
        ]);
    }

    [Fact]
    public void Ticks_OverCap_Returns_TickLoadTooLarge()
    {
        var opts = new LoadOptions { MaxTickMonthsPerRequest = 6, MaxMonthsPerRequest = 600 };
        var req = ValidRequest() with {
            AssetType = AssetTypes.Perpetual, FeedName = FeedNames.Ticks, Interval = "",
            From = new DateOnly(2024, 1, 1), To = new DateOnly(2024, 12, 31) }; // 12 months
        var err = LoadRequestValidator.Validate(req, RegistryWithTicks(), opts);
        Assert.Equal("tick_load_too_large", err!.Code);
    }

    [Fact]
    public void Ticks_WithinCap_Passes()
    {
        var opts = new LoadOptions { MaxTickMonthsPerRequest = 24, MaxMonthsPerRequest = 600 };
        var req = ValidRequest() with {
            AssetType = AssetTypes.Perpetual, FeedName = FeedNames.Ticks,
            Interval = "", From = new DateOnly(2024, 1, 1), To = new DateOnly(2024, 3, 31) };
        Assert.Null(LoadRequestValidator.Validate(req, RegistryWithTicks(), opts));
    }

    [Fact]
    public void Ticks_EmptyInterval_NotRejectedAsInvalidInterval()
    {
        // Regression: IntervalParser.ToTimeSpan("") must NOT be reached for ticks.
        var req = ValidRequest() with { AssetType = AssetTypes.Perpetual, FeedName = FeedNames.Ticks, Interval = "" };
        var err = LoadRequestValidator.Validate(req, RegistryWithTicks(), new LoadOptions());
        Assert.True(err is null || err.Code != "invalid_interval");
    }

    [Fact]
    public void NonTickFeed_EmptyInterval_StillInvalidInterval()
    {
        var req = ValidRequest() with { FeedName = FeedNames.Candles, Interval = "" };
        Assert.Equal("invalid_interval", LoadRequestValidator.Validate(req, RegistryWithCandles(), new LoadOptions())!.Code);
    }

    [Fact]
    public void FundingRate_EmptyInterval_Passes_AndIsNotCappedAsTick()
    {
        // funding-rate is interval-less (bypasses IntervalParser) BUT is NOT subject to the tick cap.
        var req = ValidRequest() with {
            AssetType = AssetTypes.Perpetual, FeedName = FeedNames.FundingRate,
            Interval = "", From = new DateOnly(2020, 1, 1), To = new DateOnly(2024, 12, 31) }; // 60 months
        var err = LoadRequestValidator.Validate(req, RegistryWithFunding(),
            new LoadOptions { MaxTickMonthsPerRequest = 24 });
        Assert.Null(err); // neither invalid_interval nor tick_load_too_large
    }

    // -------------------------------------------------------------------------
    // Path traversal: PostLoad must return 422 without touching the filesystem.
    // -------------------------------------------------------------------------

    private static IOptionsMonitor<HistoryLoaderOptions> DefaultMonitor()
    {
        var m = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        m.CurrentValue.Returns(new HistoryLoaderOptions { Load = DefaultOptions });
        return m;
    }

    [Theory]
    [InlineData("../evil", "BTCUSDT")]
    [InlineData("bin\\..\\..", "BTCUSDT")]
    [InlineData("binance", "../secrets")]
    [InlineData("binance", "BTC/USDT")]
    public void PostLoad_TraversalInExchangeOrSymbol_Returns422_NoJobEnqueued(string exchange, string symbol)
    {
        var loadRegistry = Substitute.For<ILoadJobRegistry>();
        var req = ValidRequest() with { Exchange = exchange, Symbol = symbol };

        var result = LoadEndpoints.PostLoad(
            req, DefaultMonitor(), RegistryWithCandles(), loadRegistry);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
        loadRegistry.DidNotReceiveWithAnyArgs().TryEnqueue(default!, default!);
    }
}
