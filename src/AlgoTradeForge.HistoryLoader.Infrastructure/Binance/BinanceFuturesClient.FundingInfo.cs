using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient
{
    private const int FundingInfoWeight = 1;

    Task<IReadOnlyList<FundingInfoEntry>> IFundingInfoFetcher.FetchAsync(CancellationToken ct) =>
        FetchFundingInfoAsync(ct);

    /// <summary>
    /// Fetches the symbol-wide funding-rate cap/floor table from
    /// <c>/fapi/v1/fundingInfo</c>. Single-shot endpoint — returns every symbol with
    /// adjusted-funding configuration; symbols without overrides are omitted.
    /// </summary>
    public async Task<IReadOnlyList<FundingInfoEntry>> FetchFundingInfoAsync(CancellationToken ct)
    {
        var url = $"{options.FuturesBaseUrl}/fapi/v1/fundingInfo";
        return await BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, FundingInfoWeight, ParseFundingInfoBatch, ct).ConfigureAwait(false);
    }

    private static FundingInfoEntry[] ParseFundingInfoBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var entries = new FundingInfoEntry[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            var symbol = element.GetProperty("symbol").GetString();
            if (string.IsNullOrEmpty(symbol))
                continue;

            if (!BinanceJsonHelper.TryParseDouble(element, "adjustedFundingRateCap", out var cap))
                continue;
            if (!BinanceJsonHelper.TryParseDouble(element, "adjustedFundingRateFloor", out var floor))
                continue;

            int intervalHours = element.TryGetProperty("fundingIntervalHours", out var ihEl)
                ? ihEl.GetInt32()
                : 8;

            bool disclaimer = element.TryGetProperty("disclaimer", out var dEl)
                && dEl.ValueKind == JsonValueKind.True;

            entries[i++] = new FundingInfoEntry(symbol, cap, floor, intervalHours, disclaimer);
        }

        return entries.AsSpan(0, i).ToArray();
    }
}
