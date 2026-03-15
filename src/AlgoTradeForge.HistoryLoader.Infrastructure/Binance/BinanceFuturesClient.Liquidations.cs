using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal sealed partial class BinanceFuturesClient
{
    private const int LiquidationLimit = 1000;
    private const int LiquidationWeight = 1;

    /// <summary>
    /// Fetches forced liquidation order history from the Binance USDT-M Futures API
    /// for the given <paramref name="symbol"/> over the time range
    /// [<paramref name="fromMs"/>, <paramref name="toMs"/>).
    /// </summary>
    /// <remarks>
    /// Each <see cref="FeedRecord"/> contains four values:
    /// side (index 0), averagePrice (index 1), executedQty (index 2), notional_usd (index 3).
    /// Side encoding: 1.0 = long liquidated (SELL order), -1.0 = short liquidated (BUY order).
    /// Columns: <c>["side", "price", "qty", "notional_usd"]</c>.
    /// </remarks>
    public async IAsyncEnumerable<FeedRecord> FetchLiquidationsAsync(
        string symbol,
        long fromMs,
        long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long cursor = fromMs;

        while (cursor < toMs)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await FetchLiquidationBatchWithRetryAsync(symbol, cursor, toMs, ct)
                .ConfigureAwait(false);

            if (batch.Length == 0)
                yield break;

            foreach (var record in batch)
                yield return record;

            if (batch.Length < LiquidationLimit)
                yield break;

            // Advance cursor past the last received timestamp to avoid duplicates
            cursor = batch[^1].TimestampMs + 1;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers — liquidations
    // -------------------------------------------------------------------------

    private Task<FeedRecord[]> FetchLiquidationBatchWithRetryAsync(
        string symbol,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var url = BuildLiquidationUrl(symbol, fromMs, toMs);
        return BinanceRetryHelper.FetchWithRetryAsync(
            httpClient, rateLimiter, options.RequestDelayMs,
            url, LiquidationWeight, ParseLiquidationBatch, ct);
    }

    private string BuildLiquidationUrl(string symbol, long fromMs, long toMs) =>
        $"{options.FuturesBaseUrl}/fapi/v1/allForceOrders" +
        $"?symbol={symbol}" +
        $"&startTime={fromMs}&endTime={toMs}&limit={LiquidationLimit}";

    private static FeedRecord[] ParseLiquidationBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new FeedRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            var order = element.GetProperty("o");

            long time = order.GetProperty("time").GetInt64();
            string side = BinanceJsonHelper.ParseRequiredString(order, "side");
            double averagePrice = double.Parse(
                BinanceJsonHelper.ParseRequiredString(order, "averagePrice"),
                CultureInfo.InvariantCulture);
            double executedQty = double.Parse(
                BinanceJsonHelper.ParseRequiredString(order, "executedQty"),
                CultureInfo.InvariantCulture);

            // SELL order = long position liquidated → 1.0
            // BUY order  = short position liquidated → -1.0
            double sideValue = side == "SELL" ? 1.0 : -1.0;
            double notionalUsd = executedQty * averagePrice;

            records[i++] = new FeedRecord(time, [sideValue, averagePrice, executedQty, notionalUsd]);
        }

        return records;
    }
}
