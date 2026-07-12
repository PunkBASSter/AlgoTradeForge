using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Domain.Symbology;

namespace AlgoTradeForge.HistoryLoader.Tests.TestData;

internal static class CollectionAssets
{
    internal static CollectionAsset Perp(string apiSymbol = "BTCUSDT", int digits = 2,
        params CollectionFeed[] feeds) =>
        new("binance", $"{apiSymbol[..^4]}/USDT-PERP",
            new VenueInstrument(apiSymbol, AssetTypes.Perpetual, $"{apiSymbol}_perp"),
            digits, feeds);

    internal static CollectionAsset Spot(string apiSymbol = "BTCUSDT", int digits = 2,
        params CollectionFeed[] feeds) =>
        new("binance", $"{apiSymbol[..^4]}/USDT",
            new VenueInstrument(apiSymbol, AssetTypes.Spot, apiSymbol),
            digits, feeds);

    internal static CollectionFeed Feed(string name, string interval = "", string collect = "eager",
        DateOnly? start = null) =>
        new(name, interval, collect, "csv", start ?? new DateOnly(2024, 1, 1));
}
