using System.Globalization;
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
            decimal open     = decimal.Parse(BinanceJsonHelper.ParseRequiredString(row[1], 1), CultureInfo.InvariantCulture);
            decimal high     = decimal.Parse(BinanceJsonHelper.ParseRequiredString(row[2], 2), CultureInfo.InvariantCulture);
            decimal low      = decimal.Parse(BinanceJsonHelper.ParseRequiredString(row[3], 3), CultureInfo.InvariantCulture);
            decimal close    = decimal.Parse(BinanceJsonHelper.ParseRequiredString(row[4], 4), CultureInfo.InvariantCulture);
            decimal volume   = decimal.Parse(BinanceJsonHelper.ParseRequiredString(row[5], 5), CultureInfo.InvariantCulture);
            // row[6] = close time (unused directly)
            double quoteVolume      = double.Parse(BinanceJsonHelper.ParseRequiredString(row[7], 7), CultureInfo.InvariantCulture);
            double tradeCount       = row[8].GetInt32();
            double takerBuyVolume   = double.Parse(BinanceJsonHelper.ParseRequiredString(row[9], 9),  CultureInfo.InvariantCulture);
            double takerBuyQuoteVol = double.Parse(BinanceJsonHelper.ParseRequiredString(row[10], 10), CultureInfo.InvariantCulture);

            records[i++] = new CandleRecord(
                timestampMs, open, high, low, close, volume)
            {
                ExtValues = [quoteVolume, tradeCount, takerBuyVolume, takerBuyQuoteVol]
            };
        }

        return records;
    }
}
