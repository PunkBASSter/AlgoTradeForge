using System.Runtime.CompilerServices;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed class BinanceSpotClient(HttpClient httpClient, BinanceOptions options, SourceRateLimiter rateLimiter)
    : ICandleFetcher
{
    private const int KlineLimit = 1000;
    private const int KlineWeight = 2;
    private const int AggTradeLimit = 1000;
    private const int AggTradeWeight = 4;

    public string[]? CandleExtColumns => null;

    // -------------------------------------------------------------------------
    // Klines
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches klines (candlestick bars) from the Binance Spot API
    /// for the given <paramref name="symbol"/> and <paramref name="interval"/>
    /// over the half-open time range [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// </summary>
    public async IAsyncEnumerable<CandleRecord> FetchCandlesAsync(
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

    private Task<CandleRecord[]> FetchKlineBatchWithRetryAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var url = BuildKlineUrl(symbol, interval, fromMs, toMs);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, KlineWeight, BinanceKlineParser.ParseBatch, ct);
    }

    private string BuildKlineUrl(string symbol, string interval, long fromMs, long toMs) =>
        $"{options.SpotBaseUrl}/api/v3/klines" +
        $"?symbol={symbol}&interval={interval}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={KlineLimit}";

    // -------------------------------------------------------------------------
    // Aggregate trades
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches aggregate trades for [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// First page is time-bounded; subsequent pages use <c>fromId</c> pagination so we walk
    /// every trade even when &gt;1000 share a single millisecond (a <c>ts+1</c> cursor would
    /// drop overflow). <c>fromId</c>-bounded responses ignore <c>endTime</c>, so trades past
    /// <paramref name="toMs"/> are trimmed client-side.
    /// </summary>
    public async IAsyncEnumerable<FeedRecord> FetchAggTradesAsync(
        string symbol,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var firstBatch = await FetchAggTradeBatchByTimeWithRetryAsync(symbol, fromMs, toMs, ct)
            .ConfigureAwait(false);

        if (firstBatch.Length == 0)
            yield break;

        long? lastAggId = null;
        foreach (var record in firstBatch)
        {
            if (record.TimestampMs > toMs)
                yield break;
            yield return record;
            lastAggId = (long)record.Values[3];
        }

        if (firstBatch.Length < AggTradeLimit || lastAggId is null)
            yield break;

        while (lastAggId is { } prevLastId)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await FetchAggTradeBatchByIdWithRetryAsync(symbol, prevLastId + 1, ct)
                .ConfigureAwait(false);

            if (batch.Length == 0)
                yield break;

            // aggIds must be strictly monotonic. If the next batch's first id doesn't exceed
            // the previous tail, suspect a per-symbol id reset (rare). Bail rather than loop.
            long firstAggId = (long)batch[0].Values[3];
            if (firstAggId <= prevLastId)
                yield break;

            long? newLastAggId = null;
            foreach (var record in batch)
            {
                if (record.TimestampMs > toMs)
                    yield break;
                yield return record;
                newLastAggId = (long)record.Values[3];
            }

            if (batch.Length < AggTradeLimit)
                yield break;

            lastAggId = newLastAggId;
        }
    }

    private Task<FeedRecord[]> FetchAggTradeBatchByTimeWithRetryAsync(
        string symbol,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var url = BuildAggTradeUrlByTime(symbol, fromMs, toMs);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, AggTradeWeight, BinanceAggTradeParser.ParseBatch, ct);
    }

    private Task<FeedRecord[]> FetchAggTradeBatchByIdWithRetryAsync(
        string symbol,
        long fromId,
        CancellationToken ct)
    {
        var url = BuildAggTradeUrlById(symbol, fromId);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, AggTradeWeight, BinanceAggTradeParser.ParseBatch, ct);
    }

    private string BuildAggTradeUrlByTime(string symbol, long fromMs, long toMs) =>
        $"{options.SpotBaseUrl}/api/v3/aggTrades" +
        $"?symbol={symbol}&startTime={fromMs}&endTime={toMs}&limit={AggTradeLimit}";

    private string BuildAggTradeUrlById(string symbol, long fromId) =>
        $"{options.SpotBaseUrl}/api/v3/aggTrades" +
        $"?symbol={symbol}&fromId={fromId}&limit={AggTradeLimit}";
}
