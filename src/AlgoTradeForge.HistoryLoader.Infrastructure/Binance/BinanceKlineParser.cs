using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal static class BinanceKlineParser
{
    public static CandleRecord[] ParseBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new CandleRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            var row = element.EnumerateArray().ToArray();

            long timestampMs = row[0].GetInt64();

            if (!BinanceJsonHelper.TryParseDecimal(row[1], out var open))
                continue;
            if (!BinanceJsonHelper.TryParseDecimal(row[2], out var high))
                continue;
            if (!BinanceJsonHelper.TryParseDecimal(row[3], out var low))
                continue;
            if (!BinanceJsonHelper.TryParseDecimal(row[4], out var close))
                continue;
            if (!BinanceJsonHelper.TryParseDecimal(row[5], out var volume))
                continue;
            // row[6] = close time (unused directly)
            if (!BinanceJsonHelper.TryParseDouble(row[7], out var quoteVolume))
                continue;
            double tradeCount = row[8].GetInt32();
            if (!BinanceJsonHelper.TryParseDouble(row[9], out var takerBuyVolume))
                continue;
            if (!BinanceJsonHelper.TryParseDouble(row[10], out var takerBuyQuoteVol))
                continue;

            records[i++] = new CandleRecord(
                timestampMs, open, high, low, close, volume)
            {
                ExtValues = [quoteVolume, tradeCount, takerBuyVolume, takerBuyQuoteVol]
            };
        }

        return records.AsSpan(0, i).ToArray();
    }
}
