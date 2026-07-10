namespace AlgoTradeForge.HistoryLoader.Domain.Symbology;

public sealed class BinanceSymbology : IExchangeSymbology
{
    public string Exchange => "binance";

    public bool TryResolve(CanonicalSymbol symbol, out VenueInstrument? instrument, out string? unsupportedReason)
    {
        if (symbol.Kind == InstrumentKind.DatedFuture)
        {
            instrument = null;
            unsupportedReason = "dated futures are not collectable on binance yet";
            return false;
        }

        var apiSymbol = symbol.Base + symbol.Quote;
        var assetType = symbol.Kind == InstrumentKind.Perpetual ? AssetTypes.Perpetual : AssetTypes.Spot;
        var dir = AssetPathConvention.DirectoryName(apiSymbol, assetType);

        instrument = new VenueInstrument(apiSymbol, assetType, dir);
        unsupportedReason = null;
        return true;
    }
}
