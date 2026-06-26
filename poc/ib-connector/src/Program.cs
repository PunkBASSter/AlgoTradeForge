using IBApi;

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

        // Cancels all open ORDERS account-wide (incl. bracket parent+children); does NOT flatten positions
        // a filled order leaves behind — use the "flatten" phase for that.
        if (phase == "cancel")
        {
            Log.Line("reqAllOpenOrders -> reqGlobalCancel");
            conn.Client.reqAllOpenOrders();
            await Task.Delay(TimeSpan.FromSeconds(3));
            conn.Client.reqGlobalCancel(new OrderCancel());
            await Task.Delay(TimeSpan.FromSeconds(5));   // let orderStatus=Cancelled callbacks arrive
            conn.Disconnect();
            Log.Line("done");
            return 0;
        }

        var contract = Contracts.Aapl();
        await Contracts.ResolveAsync(conn, wrapper, contract, reqId: 1);

        if (phase == "flatten")
        {
            await Orders.FlattenAsync(conn, wrapper, contract, nextOrderId);
            conn.Disconnect();
            Log.Line("done");
            return 0;
        }

        // In "all" mode each phase is independent: a single failure (e.g. a market order that
        // never fills off-hours) is logged and the run continues so every phase is still exercised.
        async Task RunPhase(string name, Func<Task> body)
        {
            try { await body(); }
            catch (Exception ex) { Log.Line($"PHASE '{name}' FAILED: {ex.GetType().Name}: {ex.Message}"); }
        }

        if (phase is "data" or "all")
            await RunPhase("data", () => MarketData.StreamAsync(conn, wrapper, contract, aggSeconds: 10,
                duration: TimeSpan.FromSeconds(30), realtime));

        if (phase is "market-order" or "all")
        {
            var id = nextOrderId++;
            await RunPhase("market-order", () => Orders.MarketRoundTripAsync(conn, wrapper, contract, id, qty: 1));
        }

        if (phase is "limit-cancel" or "all")
        {
            var id = nextOrderId++;
            await RunPhase("limit-cancel", () => Orders.LimitThenCancelAsync(conn, wrapper, contract, id, qty: 1, farLimitPrice: 1.00));
        }

        if (phase is "bracket" or "all")
        {
            var id = nextOrderId;
            nextOrderId += 3;
            await RunPhase("bracket", () => Orders.PlaceBracketAsync(conn, wrapper, contract, id, qty: 1,
                takeProfit: 10_000.0, stopLoss: 1.0));   // far-from-market so nothing fills during the spike
        }

        if (phase is "readback" or "all")
            await RunPhase("readback", () => AccountReadback.DumpAsync(conn, wrapper));

        conn.Disconnect();
        Log.Line("done");
        return 0;
    }
}
