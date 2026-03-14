using System.Globalization;
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
