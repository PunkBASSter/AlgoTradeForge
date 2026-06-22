using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Aggregation;

public static class TickToSourceRecord
{
    public static SourceRecord From(in TradeTick tick)
    {
        var buy = tick.Aggressor == AggressorSide.Buy;
        var sell = tick.Aggressor == AggressorSide.Sell;
        return new SourceRecord(
            TsMs: tick.TimestampMs,
            Open: tick.Price, High: tick.Price, Low: tick.Price, Close: tick.Price,
            Volume: tick.Quantity,
            BuyVolumeLong: buy ? tick.Quantity : 0L,
            SellVolumeLong: sell ? tick.Quantity : 0L,
            BuyTradeCountLong: buy ? 1L : 0L,
            SellTradeCountLong: sell ? 1L : 0L);
    }
}
