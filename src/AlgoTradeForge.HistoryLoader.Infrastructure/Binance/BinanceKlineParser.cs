using System.Globalization;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal static class BinanceKlineParser
{
    public static KlineRecord[] ParseBatch(string json)
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
