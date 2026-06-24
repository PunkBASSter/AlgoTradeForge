using IBApi;

namespace IbPoc;

internal static class Orders
{
    public static async Task MarketRoundTripAsync(IbConnection conn, DemoWrapper wrapper,
        Contract contract, int orderId, decimal qty)
    {
        // Tif is mandatory: IB rejects an order with an empty time-in-force (error 10052) — set DAY on every order.
        var order = new Order { Action = "BUY", OrderType = "MKT", TotalQuantity = qty, Tif = "DAY" };
        var filled = wrapper.WaitForStatusAsync(orderId, "Filled");
        Log.Line($"placeOrder MKT BUY {qty} id={orderId}");
        conn.Client.placeOrder(orderId, contract, order);
        await filled.WaitAsync(TimeSpan.FromSeconds(30));
        Log.Line($"market order {orderId} filled");
    }

    // reqGlobalCancel clears open ORDERS but never closes a POSITION; a market-hours fill leaves real shares
    // that must be flattened with an opposite-side order. Query the live position, then offset it at market.
    public static async Task FlattenAsync(IbConnection conn, DemoWrapper wrapper, Contract contract, int orderId)
    {
        wrapper.ResetPositions();
        conn.Client.reqPositions();
        await wrapper.PositionsReceived.WaitAsync(TimeSpan.FromSeconds(10));
        conn.Client.cancelPositions();

        wrapper.Positions.TryGetValue(contract.Symbol, out var qty);
        if (qty == 0) { Log.Line($"flatten: no {contract.Symbol} position to close"); return; }

        var action = qty > 0 ? "SELL" : "BUY";   // long -> sell, short -> buy
        var absQty = Math.Abs(qty);
        var order = new Order { Action = action, OrderType = "MKT", TotalQuantity = absQty, Tif = "DAY" };
        var filled = wrapper.WaitForStatusAsync(orderId, "Filled");
        Log.Line($"flatten: placeOrder MKT {action} {absQty} {contract.Symbol} id={orderId}");
        conn.Client.placeOrder(orderId, contract, order);
        await filled.WaitAsync(TimeSpan.FromSeconds(30));
        Log.Line($"flatten: {contract.Symbol} position closed");
    }

    public static async Task LimitThenCancelAsync(IbConnection conn, DemoWrapper wrapper,
        Contract contract, int orderId, decimal qty, double farLimitPrice)
    {
        var order = new Order
        {
            Action = "BUY", OrderType = "LMT", TotalQuantity = qty, LmtPrice = farLimitPrice, Tif = "DAY",
        };
        var submitted = wrapper.WaitForStatusAsync(orderId, "Submitted");
        var cancelled = wrapper.WaitForStatusAsync(orderId, "Cancelled");
        Log.Line($"placeOrder LMT BUY {qty} @ {farLimitPrice} id={orderId}");
        conn.Client.placeOrder(orderId, contract, order);
        await submitted.WaitAsync(TimeSpan.FromSeconds(20));
        Log.Line($"limit order {orderId} resting; cancelling");
        conn.Client.cancelOrder(orderId, new OrderCancel());
        await cancelled.WaitAsync(TimeSpan.FromSeconds(20));
        Log.Line($"limit order {orderId} cancelled");
    }

    public static async Task PlaceBracketAsync(IbConnection conn, DemoWrapper wrapper,
        Contract contract, int parentId, decimal qty, double takeProfit, double stopLoss)
    {
        // Standard IB bracket: parent transmits last so the children attach atomically.
        var parent = new Order
        {
            OrderId = parentId, Action = "BUY", OrderType = "MKT",
            TotalQuantity = qty, Tif = "DAY", Transmit = false,
        };
        var tp = new Order
        {
            OrderId = parentId + 1, Action = "SELL", OrderType = "LMT",
            TotalQuantity = qty, LmtPrice = takeProfit, ParentId = parentId, Tif = "DAY", Transmit = false,
        };
        var sl = new Order
        {
            OrderId = parentId + 2, Action = "SELL", OrderType = "STP",
            TotalQuantity = qty, AuxPrice = stopLoss, ParentId = parentId, Tif = "DAY", Transmit = true,
        };

        var parentAck = wrapper.WaitForStatusAsync(parentId, "PreSubmitted");
        Log.Line($"placeOrder BRACKET parent={parentId} TP={takeProfit} SL={stopLoss}");
        conn.Client.placeOrder(parent.OrderId, contract, parent);
        conn.Client.placeOrder(tp.OrderId, contract, tp);
        conn.Client.placeOrder(sl.OrderId, contract, sl);
        await Task.WhenAny(parentAck, Task.Delay(TimeSpan.FromSeconds(20)));
        Log.Line($"bracket {parentId} submitted (parent + TP + SL)");
    }
}
