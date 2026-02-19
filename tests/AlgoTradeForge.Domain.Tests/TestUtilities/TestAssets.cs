namespace AlgoTradeForge.Domain.Tests.TestUtilities;

public static class TestAssets
{
    public static Asset Aapl => Asset.Equity("AAPL", "NASDAQ");

    public static Asset Msft => Asset.Equity("MSFT", "NASDAQ");

    public static Asset EsMini => Asset.Future("ES", "CME", multiplier: 50m, tickSize: 0.25m, margin: 15000m);

    public static Asset MicroEs => Asset.Future("MES", "CME", multiplier: 5m, tickSize: 0.25m, margin: 1500m);

    public static Asset BtcUsdt => Asset.Crypto("BTCUSDT", "Binance", decimalDigits: 2, historyStart: new DateOnly(2024, 1, 1));
}
