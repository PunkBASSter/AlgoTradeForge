namespace AlgoTradeForge.HistoryLoader.Domain.Symbology;

/// <summary>Venue resolution of a canonical symbol. ApiSymbol = exchange REST/WS symbol;
/// AssetType = AssetTypes vocabulary; Dir = on-disk asset directory name.</summary>
public sealed record VenueInstrument(string ApiSymbol, string AssetType, string Dir);

public interface IExchangeSymbology
{
    string Exchange { get; }    // canonical lowercase id, e.g. "binance"

    /// <summary>Override (from group symbolOverrides) is consulted by the CALLER before this method.</summary>
    bool TryResolve(CanonicalSymbol symbol, out VenueInstrument? instrument, out string? unsupportedReason);
}
