using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient
{
    private const int TakerVolumeLimit = 500;
    private const int TakerVolumeWeight = 1;

    /// <summary>
    /// Fetches taker long/short ratio history from the Binance USDT-M Futures data API
    /// (<c>/futures/data/takerlongshortRatio</c>)
    /// for the given <paramref name="symbol"/> and <paramref name="interval"/>
    /// over the time range [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// </summary>
    /// <remarks>
    /// Each <see cref="FeedRecord"/> contains three values:
    /// buyVol (index 0), sellVol (index 1), buySellRatio (index 2).
    /// Columns: <c>["buy_vol_usd", "sell_vol_usd", "ratio"]</c>.
    /// </remarks>
    public async IAsyncEnumerable<FeedRecord> FetchTakerVolumeAsync(
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

            var batch = await FetchTakerVolumeBatchWithRetryAsync(symbol, interval, cursor, toMs, ct)
                .ConfigureAwait(false);

            if (batch.Length == 0)
                yield break;

            foreach (var record in batch)
                yield return record;

            if (batch.Length < TakerVolumeLimit)
                yield break;

            cursor = batch[^1].TimestampMs + 1;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers — taker volume
    // -------------------------------------------------------------------------

    private Task<FeedRecord[]> FetchTakerVolumeBatchWithRetryAsync(
        string symbol,
        string interval,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var url = BuildTakerVolumeUrl(symbol, interval, fromMs, toMs);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, TakerVolumeWeight, ParseTakerVolumeBatch, ct);
    }

    private string BuildTakerVolumeUrl(string symbol, string interval, long fromMs, long toMs) =>
        $"{options.FuturesBaseUrl}/futures/data/takerlongshortRatio" +
        $"?symbol={symbol}&period={interval}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={TakerVolumeLimit}";

    private static FeedRecord[] ParseTakerVolumeBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new FeedRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            var tsProp = element.GetProperty("timestamp");
            long timestamp = tsProp.ValueKind == JsonValueKind.Number
                ? tsProp.GetInt64()
                : long.Parse(tsProp.GetString()!, CultureInfo.InvariantCulture);

            if (!BinanceJsonHelper.TryParseDouble(element, "buyVol", out var buyVol))
                continue;
            if (!BinanceJsonHelper.TryParseDouble(element, "sellVol", out var sellVol))
                continue;
            if (!BinanceJsonHelper.TryParseDouble(element, "buySellRatio", out var buySellRatio))
                continue;

            records[i++] = new FeedRecord(timestamp, [buyVol, sellVol, buySellRatio]);
        }

        return records.AsSpan(0, i).ToArray();
    }
}
