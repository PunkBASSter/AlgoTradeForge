using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

/// <summary>
/// Verifies the spec classification table against the REAL materializer set constructed
/// exactly as DI does in AddHistoryLoaderInfrastructure.
/// </summary>
public sealed class BinanceClassificationTests
{
    private static ArchiveMaterializerRegistry BuildRegistry()
    {
        var archive = Substitute.For<IBinanceArchiveClient>();
        var partitionWriter = Substitute.For<IPartitionFileWriter>();
        var schemaManager = Substitute.For<ISchemaManager>();
        var feedStatusStore = Substitute.For<IFeedStatusStore>();

        return new ArchiveMaterializerRegistry(
        [
            new KlinesArchiveMaterializer(
                FeedNames.Candles, "klines", supportsSpot: true,
                archive, partitionWriter, schemaManager, feedStatusStore,
                NullLogger<KlinesArchiveMaterializer>.Instance),
            new KlinesArchiveMaterializer(
                FeedNames.MarkPrice, "markPriceKlines", supportsSpot: false,
                archive, partitionWriter, schemaManager, feedStatusStore,
                NullLogger<KlinesArchiveMaterializer>.Instance),
            new MetricsArchiveMaterializer(
                FeedNames.OpenInterest,
                archive, partitionWriter, schemaManager, feedStatusStore,
                NullLogger<MetricsArchiveMaterializer>.Instance),
            new MetricsArchiveMaterializer(
                FeedNames.LsRatioGlobal,
                archive, partitionWriter, schemaManager, feedStatusStore,
                NullLogger<MetricsArchiveMaterializer>.Instance),
            new MetricsArchiveMaterializer(
                FeedNames.LsRatioTopAccounts,
                archive, partitionWriter, schemaManager, feedStatusStore,
                NullLogger<MetricsArchiveMaterializer>.Instance),
            new MetricsArchiveMaterializer(
                FeedNames.LsRatioTopPositions,
                archive, partitionWriter, schemaManager, feedStatusStore,
                NullLogger<MetricsArchiveMaterializer>.Instance),
        ]);
    }

    [Fact]
    public void Candles_Spot_Replenishable() =>
        Assert.True(BuildRegistry().IsReplenishable("binance", FeedNames.Candles, AssetTypes.Spot));

    [Fact]
    public void Candles_Perpetual_Replenishable() =>
        Assert.True(BuildRegistry().IsReplenishable("binance", FeedNames.Candles, AssetTypes.Perpetual));

    [Fact]
    public void MarkPrice_Spot_NotReplenishable() =>
        Assert.False(BuildRegistry().IsReplenishable("binance", FeedNames.MarkPrice, AssetTypes.Spot));

    [Fact]
    public void OpenInterest_Perpetual_Replenishable() =>
        Assert.True(BuildRegistry().IsReplenishable("binance", FeedNames.OpenInterest, AssetTypes.Perpetual));

    [Fact]
    public void OpenInterest_Spot_NotReplenishable() =>
        Assert.False(BuildRegistry().IsReplenishable("binance", FeedNames.OpenInterest, AssetTypes.Spot));

    [Fact]
    public void Liquidations_NotReplenishable() =>
        Assert.False(BuildRegistry().IsReplenishable("binance", FeedNames.Liquidations, AssetTypes.Perpetual));

    [Fact]
    public void UnknownExchange_Ib_NotReplenishable() =>
        Assert.False(BuildRegistry().IsReplenishable("ib", FeedNames.Candles, AssetTypes.Equity));
}
