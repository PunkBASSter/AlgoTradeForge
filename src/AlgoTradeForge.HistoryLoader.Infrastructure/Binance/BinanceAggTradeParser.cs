using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

/// <summary>
/// Parses Binance USDT-M Futures <c>/fapi/v1/aggTrades</c> response into <see cref="FeedRecord"/>s.
/// JSON shape per element: <c>{a:aggId, p:price, q:qty, f:firstId, l:lastId, T:ts, m:isBuyerMaker, M:bestMatch}</c>.
/// Output value order matches column order <c>[price, qty, is_buyer_maker, agg_id]</c> (TRD §3.5).
/// </summary>
internal static class BinanceAggTradeParser
{
    public static FeedRecord[] ParseBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new FeedRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            long timestampMs = element.GetProperty("T").GetInt64();
            long aggId = element.GetProperty("a").GetInt64();
            bool isBuyerMaker = element.GetProperty("m").GetBoolean();

            // Malformed numeric fields are loud failures (matches PartitionedSourceReader.MalformedCell
            // and the P1b-0a tightening of CsvFeedSeriesLoader). The retry helper at
            // BinanceRetryHelper.FetchWithRetryAsync calls this parser outside its try/catch, so the
            // exception propagates to the collector without retry — a persistent schema break shouldn't
            // hammer Binance.
            if (!BinanceJsonHelper.TryParseDouble(element, "p", out var price))
                throw MalformedField(aggId, "p", element);
            if (!BinanceJsonHelper.TryParseDouble(element, "q", out var qty))
                throw MalformedField(aggId, "q", element);

            records[i++] = new FeedRecord(
                timestampMs,
                [price, qty, isBuyerMaker ? 1.0 : 0.0, aggId]);
        }

        return records;
    }

    private static FormatException MalformedField(long aggId, string field, JsonElement element) =>
        new($"Malformed Binance aggTrade field '{field}' for aggId={aggId}: " +
            $"raw='{(element.TryGetProperty(field, out var p) ? p.ToString() : "<missing>")}'.");
}
