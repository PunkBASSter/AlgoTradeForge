using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed class BinanceSpotClient(HttpClient httpClient, BinanceOptions options, SourceRateLimiter rateLimiter)
    : ISpotDataFetcher
{
    private const int KlineLimit = 1000;
    private const int KlineWeight = 2;

    // -------------------------------------------------------------------------
    // Klines
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches klines (candlestick bars) from the Binance Spot API
    /// for the given <paramref name="symbol"/> and <paramref name="interval"/>
    /// over the half-open time range [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// </summary>
    public async IAsyncEnumerable<KlineRecord> FetchKlinesAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cursor = fromMs;

        while (cursor < toMs)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await FetchKlineBatchWithRetryAsync(symbol, interval, cursor, toMs, ct)
                .ConfigureAwait(false);

            if (batch.Length == 0)
                yield break;

            foreach (var record in batch)
                yield return record;

            if (batch.Length < KlineLimit)
                yield break;

            cursor = batch[^1].TimestampMs + 1;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private Task<KlineRecord[]> FetchKlineBatchWithRetryAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var url = BuildKlineUrl(symbol, interval, fromMs, toMs);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, KlineWeight, ParseKlineBatch, ct);
    }

    private string BuildKlineUrl(string symbol, string interval, long fromMs, long toMs) =>
        $"{options.SpotBaseUrl}/api/v3/klines" +
        $"?symbol={symbol}&interval={interval}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={KlineLimit}";

    private static KlineRecord[] ParseKlineBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new KlineRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            var row = element.EnumerateArray().ToArray();

            long timestampMs = row[0].GetInt64();
            decimal open     = decimal.Parse(row[1].GetString()!, CultureInfo.InvariantCulture);
            decimal high     = decimal.Parse(row[2].GetString()!, CultureInfo.InvariantCulture);
            decimal low      = decimal.Parse(row[3].GetString()!, CultureInfo.InvariantCulture);
            decimal close    = decimal.Parse(row[4].GetString()!, CultureInfo.InvariantCulture);
            decimal volume   = decimal.Parse(row[5].GetString()!, CultureInfo.InvariantCulture);
            // row[6] = close time (unused directly)
            decimal quoteVolume      = decimal.Parse(row[7].GetString()!, CultureInfo.InvariantCulture);
            int     tradeCount       = row[8].GetInt32();
            decimal takerBuyVolume   = decimal.Parse(row[9].GetString()!,  CultureInfo.InvariantCulture);
            decimal takerBuyQuoteVol = decimal.Parse(row[10].GetString()!, CultureInfo.InvariantCulture);

            records[i++] = new KlineRecord(
                timestampMs, open, high, low, close, volume,
                quoteVolume, tradeCount, takerBuyVolume, takerBuyQuoteVol);
        }

        return records;
    }
}
