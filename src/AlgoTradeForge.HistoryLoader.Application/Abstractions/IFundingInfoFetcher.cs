using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

/// <summary>Fetches the symbol-wide funding-rate cap/floor table from the venue (e.g. Binance <c>/fapi/v1/fundingInfo</c>).</summary>
public interface IFundingInfoFetcher
{
    Task<IReadOnlyList<FundingInfoEntry>> FetchAsync(CancellationToken ct);
}
