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

            // Binance kline does NOT carry per-side trade counts. We synthesize
            // taker_buy_trade_count as a volume-weighted proxy: assumes equal-sized trades
            // within the bar, so trade_count is split by the taker_buy_vol / vol ratio.
            // EqIT-on-time-bar consumes this column; the FE surfaces a yellow banner
            // (AltBarWarnings.TimeBarTibApproximation) flagging the assumption. Clamped to
            // [0, trade_count] to keep downstream invariants intact.
            double takerBuyTradeCount = 0d;
            if ((double)volume > 0d)
            {
                var proxy = Math.Round(tradeCount * takerBuyVolume / (double)volume,
                    MidpointRounding.AwayFromZero);
                if (proxy < 0d) proxy = 0d;
                if (proxy > tradeCount) proxy = tradeCount;
                takerBuyTradeCount = proxy;
            }

            records[i++] = new CandleRecord(
                timestampMs, open, high, low, close, volume)
            {
                ExtValues = [quoteVolume, tradeCount, takerBuyVolume, takerBuyQuoteVol, takerBuyTradeCount]
            };
        }

        return records.AsSpan(0, i).ToArray();
    }
}
