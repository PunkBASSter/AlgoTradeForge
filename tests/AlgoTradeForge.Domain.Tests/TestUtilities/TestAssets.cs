namespace AlgoTradeForge.Domain.Tests.TestUtilities;

public static class TestAssets
{
    public static EquityAsset Aapl => new() { Name = "AAPL", Exchange = "NASDAQ" };

    public static EquityAsset Msft => new() { Name = "MSFT", Exchange = "NASDAQ" };

    public static FutureAsset EsMini => new()
    {
        Name = "ES", Exchange = "CME", Multiplier = 50m, TickSize = 0.25m, MarginRequirement = 15000m,
    };

    public static FutureAsset MicroEs => new()
    {
        Name = "MES", Exchange = "CME", Multiplier = 5m, TickSize = 0.25m, MarginRequirement = 1500m,
    };

    public static CryptoAsset BtcUsdt => CryptoAsset.Create("BTCUSDT", "Binance", decimalDigits: 2,
        minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    public static CryptoPerpetualAsset BtcUsdtPerp => CryptoPerpetualAsset.Create("BTCUSDT_PERP", "Binance",
        decimalDigits: 2, margin: 0.1m,
        minOrderQuantity: 0.001m, maxOrderQuantity: 1000m, quantityStepSize: 0.001m);
}
