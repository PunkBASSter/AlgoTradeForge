using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient
{
    private const int FundingRateLimit = 1000;
    private const int FundingRateWeight = 1;

    /// <summary>
    /// Fetches funding rates from the Binance USDT-M Futures API for the given
    /// <paramref name="symbol"/> over the time range
    /// [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// </summary>
    /// <remarks>
    /// Each <see cref="FeedRecord"/> contains two values: fundingRate (index 0)
    /// and markPrice (index 1).
    /// </remarks>
    public async IAsyncEnumerable<FeedRecord> FetchFundingRatesAsync(
        string symbol,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cursor = fromMs;

        while (cursor < toMs)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await FetchFundingRateBatchWithRetryAsync(symbol, cursor, toMs, ct)
                .ConfigureAwait(false);

            if (batch.Length == 0)
                yield break;

            foreach (var record in batch)
                yield return record;

            if (batch.Length < FundingRateLimit)
                yield break;

            cursor = batch[^1].TimestampMs + 1;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers — funding rate
    // -------------------------------------------------------------------------

    private Task<FeedRecord[]> FetchFundingRateBatchWithRetryAsync(
        string symbol,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var url = BuildFundingRateUrl(symbol, fromMs, toMs);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, FundingRateWeight, ParseFundingRateBatch, ct);
    }

    private string BuildFundingRateUrl(string symbol, long fromMs, long toMs) =>
        $"{options.FuturesBaseUrl}/fapi/v1/fundingRate" +
        $"?symbol={symbol}&startTime={fromMs}&endTime={toMs}&limit={FundingRateLimit}";

    private static FeedRecord[] ParseFundingRateBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new FeedRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            long fundingTime = element.GetProperty("fundingTime").GetInt64();

            if (!BinanceJsonHelper.TryParseDouble(element, "fundingRate", out var fundingRate))
                continue;
            if (!BinanceJsonHelper.TryParseDouble(element, "markPrice", out var markPrice))
                continue;

            records[i++] = new FeedRecord(fundingTime, [fundingRate, markPrice]);
        }

        return records.AsSpan(0, i).ToArray();
    }
}
