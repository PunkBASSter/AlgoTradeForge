namespace IbPoc;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var phase = args.Length > 0 ? args[0]
            : Environment.GetEnvironmentVariable("IB_PHASE") ?? "all";
        Log.Line($"phase '{phase}'");

        var wrapper = new DemoWrapper();
        var host = Environment.GetEnvironmentVariable("IB_HOST") ?? "ib-gateway";
        var port = int.Parse(Environment.GetEnvironmentVariable("IB_PORT") ?? "4002");
        var clientId = int.Parse(Environment.GetEnvironmentVariable("IB_CLIENT_ID") ?? "10");
        var realtime = (Environment.GetEnvironmentVariable("IB_REALTIME") ?? "true") == "true";

        await using var conn = new IbConnection(wrapper, host, port, clientId);
        await conn.ConnectAsync();

        // The first valid order id handed back at connect; incremented per order placed.
        var nextOrderId = await wrapper.NextValidIdAsync;

        if (phase == "connect")
        {
            conn.Disconnect();
            return 0;
        }

        var contract = Contracts.Aapl();
        await Contracts.ResolveAsync(conn, wrapper, contract, reqId: 1);

        if (phase is "data" or "all")
            await MarketData.StreamAsync(conn, wrapper, contract, aggSeconds: 10,
                duration: TimeSpan.FromSeconds(30), realtime);

        if (phase is "market-order" or "all")
            await Orders.MarketRoundTripAsync(conn, wrapper, contract, nextOrderId++, qty: 1);

        if (phase is "limit-cancel" or "all")
            await Orders.LimitThenCancelAsync(conn, wrapper, contract, nextOrderId++, qty: 1, farLimitPrice: 1.00);

        if (phase is "bracket" or "all")
        {
            await Orders.PlaceBracketAsync(conn, wrapper, contract, nextOrderId, qty: 1,
                takeProfit: 10_000.0, stopLoss: 1.0);   // far-from-market so nothing fills during the spike
            nextOrderId += 3;
        }

        if (phase is "readback" or "all")
            await AccountReadback.DumpAsync(conn, wrapper);

        conn.Disconnect();
        Log.Line("done");
        return 0;
    }
}
