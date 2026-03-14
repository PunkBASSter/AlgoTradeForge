using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient
{
    private const int PositionRatioLimit = 500;
    private const int PositionRatioWeight = 1;

    /// <summary>
    /// Fetches top trader long/short position ratio history from the Binance USDT-M Futures
    /// data API for the given <paramref name="symbol"/> and <paramref name="interval"/>
    /// over the time range [<paramref name="fromMs"/>, <paramref name="toMs"/>].
    /// </summary>
    /// <remarks>
    /// Each <see cref="FeedRecord"/> contains three values:
    /// longAccount (index 0), shortAccount (index 1), longShortRatio (index 2).
    /// Columns: <c>["long_pct", "short_pct", "ratio"]</c>.
    /// </remarks>
    public async IAsyncEnumerable<FeedRecord> FetchTopPositionRatioAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cursor = fromMs;

        while (cursor <= toMs)
        {
            ct.ThrowIfCancellationRequested();

            var url = BuildTopPositionRatioUrl(symbol, interval, cursor, toMs);
            var batch = await FetchPositionRatioBatchWithRetryAsync(url, ct).ConfigureAwait(false);

            if (batch.Length == 0)
                yield break;

            foreach (var record in batch)
                yield return record;

            if (batch.Length < PositionRatioLimit)
                yield break;

            cursor = batch[^1].TimestampMs + 1;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers — position ratio feed
    // -------------------------------------------------------------------------

    private Task<FeedRecord[]> FetchPositionRatioBatchWithRetryAsync(
        string url,
        CancellationToken ct)
    {
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, PositionRatioWeight, ParsePositionRatioBatch, ct);
    }

    private string BuildTopPositionRatioUrl(string symbol, string interval, long fromMs, long toMs) =>
        $"{options.FuturesBaseUrl}/futures/data/topLongShortPositionRatio" +
        $"?symbol={symbol}&period={interval}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={PositionRatioLimit}";

    private static FeedRecord[] ParsePositionRatioBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new FeedRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            long timestamp = element.GetProperty("timestamp").GetInt64();
            double longAccount = double.Parse(
                element.GetProperty("longAccount").GetString()!,
                CultureInfo.InvariantCulture);
            double shortAccount = double.Parse(
                element.GetProperty("shortAccount").GetString()!,
                CultureInfo.InvariantCulture);
            double longShortRatio = double.Parse(
                element.GetProperty("longShortRatio").GetString()!,
                CultureInfo.InvariantCulture);

            records[i++] = new FeedRecord(timestamp, [longAccount, shortAccount, longShortRatio]);
        }

        return records;
    }
}
