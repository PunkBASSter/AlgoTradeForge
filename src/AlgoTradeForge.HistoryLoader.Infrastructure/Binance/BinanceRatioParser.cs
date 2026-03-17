using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

internal static class BinanceRatioParser
{
    public static FeedRecord[] ParseBatch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var records = new FeedRecord[root.GetArrayLength()];
        int i = 0;

        foreach (var element in root.EnumerateArray())
        {
            long timestamp = element.GetProperty("timestamp").GetInt64();

            if (!BinanceJsonHelper.TryParseDouble(element, "longAccount", out var longAccount))
                continue;
            if (!BinanceJsonHelper.TryParseDouble(element, "shortAccount", out var shortAccount))
                continue;
            if (!BinanceJsonHelper.TryParseDouble(element, "longShortRatio", out var longShortRatio))
                continue;

            records[i++] = new FeedRecord(timestamp, [longAccount, shortAccount, longShortRatio]);
        }

        return records.AsSpan(0, i).ToArray();
    }
}
