using AlgoTradeForge.Domain;

namespace AlgoTradeForge.Application.Tests.TestUtilities;

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

    public static CryptoAsset EthUsdt => CryptoAsset.Create("ETHUSDT", "Binance", decimalDigits: 2,
        minOrderQuantity: 0.001m, maxOrderQuantity: 10000m, quantityStepSize: 0.001m);

    public static CryptoAsset SolUsdt => CryptoAsset.Create("SOLUSDT", "Binance", decimalDigits: 4,
        minOrderQuantity: 0.01m, maxOrderQuantity: 100000m, quantityStepSize: 0.01m);
}
