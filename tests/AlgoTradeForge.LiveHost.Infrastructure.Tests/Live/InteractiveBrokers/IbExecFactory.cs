using IBApi;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

// Builds IBApi.Execution / Contract for IbWrapper order-callback tests.
internal static class IbExecFactory
{
    public static Execution Make(int orderId, string execId, decimal shares, double price,
        string side = "BOT", string time = "") =>
        new()
        {
            OrderId = orderId,
            ExecId = execId,
            Shares = shares,
            Price = price,
            Side = side,
            Time = time,
        };

    public static Contract Contract(string symbol = "AAPL") =>
        new() { Symbol = symbol };
}
