using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

internal static class IbConnectionTestFactory
{
    public static IbConnection WithSeededNextValidId(int seed)
    {
        var options = new IbConnectionOptions("localhost", 4004, 0);
        var conn = new IbConnection(new IbWrapper(), options);
        conn.SeedNextOrderId(seed);
        return conn;
    }
}
