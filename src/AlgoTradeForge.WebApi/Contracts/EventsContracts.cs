namespace AlgoTradeForge.WebApi.Contracts;

/// <summary>
/// Bulk chart payload for a completed run (report-page price chart): candles reloaded from
/// history for the finest primary time-bar subscription, round-trip trades reconstructed from
/// the run folder's event log, indicator series (empty unless the run exported them — debug
/// sessions only). Times are unix SECONDS (lightweight-charts convention), prices decimal.
/// </summary>
public sealed record EventsDataResponse(
    IReadOnlyList<CandlePointResponse> Candles,
    IReadOnlyDictionary<string, object> Indicators,
    IReadOnlyList<TradeMarkerResponse> Trades);

public sealed record CandlePointResponse(
    long Time, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

public sealed record TradeMarkerResponse(
    long EntryTime,
    decimal EntryPrice,
    long? ExitTime,
    decimal? ExitPrice,
    string Side,
    decimal Quantity,
    decimal? Pnl,
    decimal Commission,
    decimal? TakeProfitPrice,
    decimal? StopLossPrice);
