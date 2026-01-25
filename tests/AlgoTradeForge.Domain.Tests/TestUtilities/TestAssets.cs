using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Tests.TestUtilities;

public static class TestAssets
{
    public static Asset Aapl => Asset.Equity("AAPL");

    public static Asset Msft => Asset.Equity("MSFT");

    public static Asset EsMini => Asset.Future("ES", multiplier: 50m, tickSize: 0.25m, margin: 15000m);

    public static Asset MicroEs => Asset.Future("MES", multiplier: 5m, tickSize: 0.25m, margin: 1500m);
}
