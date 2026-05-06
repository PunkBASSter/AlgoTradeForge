namespace AlgoTradeForge.HistoryLoader.Domain;

/// <summary>Per-symbol funding rate caps from Binance <c>/fapi/v1/fundingInfo</c>. Rates are fractional (0.0075 = 0.75%).</summary>
public sealed record FundingInfoEntry(
    string Symbol,
    double AdjustedFundingRateCap,
    double AdjustedFundingRateFloor,
    int FundingIntervalHours,
    bool Disclaimer);
