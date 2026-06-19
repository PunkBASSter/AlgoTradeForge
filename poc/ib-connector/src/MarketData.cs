using IBApi;

namespace IbPoc;

internal static class MarketData
{
    public static async Task StreamAsync(IbConnection conn, DemoWrapper wrapper, Contract contract,
        int aggSeconds, TimeSpan duration, bool realtime)
    {
        // 1 = real-time, 4 = delayed-frozen (works off-hours / without a subscription).
        conn.Client.reqMarketDataType(realtime ? 1 : 4);

        var agg = new CandleAggregator(aggSeconds);
        wrapper.OnTrade += tick =>
        {
            var completed = agg.Add(tick);
            if (completed is not null)
                Log.Line($"AGG candle start={completed.BucketStartMs} O={completed.Open} H={completed.High} " +
                         $"L={completed.Low} C={completed.Close} V={completed.Volume} ticks={completed.TickCount}");
        };
        wrapper.OnRealtimeBar += bar =>
            Log.Line($"RTB 5s O={bar.Open} H={bar.High} L={bar.Low} C={bar.Close} V={bar.Volume}");

        const int tickReqId = 101, rtbReqId = 102, histReqId = 103;
        conn.Client.reqTickByTickData(tickReqId, contract, "AllLast", 0, false);
        conn.Client.reqRealTimeBars(rtbReqId, contract, 5, "TRADES", false, null);

        if (!realtime)
        {
            // Off-hours candle path: 1 day of 5s bars, streaming updates.
            conn.Client.reqHistoricalData(histReqId, contract, "", "1 D", "5 secs", "TRADES",
                useRTH: 0, formatDate: 2, keepUpToDate: true, chartOptions: null);
        }

        Log.Line($"streaming for {duration.TotalSeconds:0}s (realtime={realtime})");
        await Task.Delay(duration);

        conn.Client.cancelTickByTickData(tickReqId);
        conn.Client.cancelRealTimeBars(rtbReqId);
        if (!realtime) conn.Client.cancelHistoricalData(histReqId);
        var flushed = agg.Flush();
        if (flushed is not null) Log.Line($"AGG final candle C={flushed.Close} ticks={flushed.TickCount}");
    }
}
