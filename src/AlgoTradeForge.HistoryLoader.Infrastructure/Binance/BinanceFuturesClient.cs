using System.Runtime.CompilerServices;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.RateLimiting;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient(
    HttpClient httpClient,
    BinanceOptions options,
    SourceRateLimiter rateLimiter)
    : ICandleFetcher, IMarkPriceCandleFetcher, IFundingRateFetcher,
      IOpenInterestFetcher, ILongShortRatioFetcher, ITakerVolumeFetcher,
      ILiquidationFetcher
{
    private const int KlineLimit = 1500;
    private const int KlineWeight = 5;

    public string[]? CandleExtColumns =>
        ["quote_vol", "trade_count", "taker_buy_vol", "taker_buy_quote_vol"];

    // -------------------------------------------------------------------------
    // Klines
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches klines (candlestick bars) from the Binance USDT-M Futures API
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
        $"{options.FuturesBaseUrl}/fapi/v1/klines" +
        $"?symbol={symbol}&interval={interval}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={KlineLimit}";
}
