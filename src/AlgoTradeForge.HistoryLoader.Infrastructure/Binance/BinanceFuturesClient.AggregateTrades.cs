using System.Runtime.CompilerServices;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient
{
    private const int AggTradeLimit = 1000;
    private const int AggTradeWeight = 20;

    /// <summary>
    /// Fetches aggregate trades (ticks) from the Binance USDT-M Futures API for the given
    /// <paramref name="symbol"/> over the half-open time range
    /// [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each <see cref="FeedRecord"/> contains four values: price (index 0), qty (index 1),
    /// is_buyer_maker (index 2: 0 or 1), agg_id (index 3).
    /// </para>
    /// <para>
    /// <b>Pagination strategy.</b> The first page is fetched by time bounds
    /// (<c>startTime</c>/<c>endTime</c>). Subsequent pages use <c>fromId</c> pagination —
    /// Binance ignores time bounds when <c>fromId</c> is present, but more importantly, a
    /// single millisecond can hold &gt;1000 trades during volatility bursts. The previous
    /// <c>cursor = batch[^1].TimestampMs + 1</c> approach silently dropped overflow trades
    /// in that ms; <c>fromId = lastAggId + 1</c> guarantees we walk every trade.
    /// </para>
    /// <para>
    /// Because <c>fromId</c>-bounded responses ignore <c>endTime</c>, this method trims
    /// records past <paramref name="toMs"/> client-side before yielding.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<FeedRecord> FetchAggTradesAsync(
        string symbol,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // First page: time-bounded.
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

        // Subsequent pages: fromId-bounded. Walks every trade past the previous batch's tail
        // even when 1000+ trades share a single millisecond.
        while (lastAggId is { } prevLastId)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await FetchAggTradeBatchByIdWithRetryAsync(symbol, prevLastId + 1, ct)
                .ConfigureAwait(false);

            if (batch.Length == 0)
                yield break;

            // Sanity: aggIds must be strictly monotonic. If the next batch's first id does not
            // exceed the previous tail, suspect a per-symbol id reset (rare — Binance has done
            // this on delisting/relisting). Bail loudly rather than loop.
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

    // -------------------------------------------------------------------------
    // Private helpers — aggregate trades
    // -------------------------------------------------------------------------

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
        $"{options.FuturesBaseUrl}/fapi/v1/aggTrades" +
        $"?symbol={symbol}&startTime={fromMs}&endTime={toMs}&limit={AggTradeLimit}";

    private string BuildAggTradeUrlById(string symbol, long fromId) =>
        $"{options.FuturesBaseUrl}/fapi/v1/aggTrades" +
        $"?symbol={symbol}&fromId={fromId}&limit={AggTradeLimit}";
}
