using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

/// <summary>
/// Per-instrument scale exponents for the relay ingest plane.
/// Price and quantity use independent power-of-ten exponents so a lossy
/// "scale everything with the price tick" shortcut is structurally impossible.
/// </summary>
public readonly record struct TickScale(byte PriceExp, byte QtyExp)
{
    public long ScalePrice(decimal price) => MoneyConvert.ToLong(price * Pow10(PriceExp));
    public long ScaleQty(decimal qty)     => MoneyConvert.ToLong(qty   * Pow10(QtyExp));

    private static decimal Pow10(byte exp)
    {
        decimal r = 1m;
        for (int i = 0; i < exp; i++) r *= 10m;
        return r;
    }
}
