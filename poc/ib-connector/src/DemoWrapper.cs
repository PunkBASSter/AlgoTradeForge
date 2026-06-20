using System.Collections.Concurrent;
using IBApi;

namespace IbPoc;

// Derives from DefaultEWrapper (vendored) so only the callbacks we exercise are overridden;
// every other EWrapper member (incl. 10.45 *ProtoBuf variants) inherits an empty body.
internal sealed class DemoWrapper : DefaultEWrapper
{
    private readonly TaskCompletionSource<int> _nextValidId =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<int>> _conIdByReq = new();
    private readonly ConcurrentDictionary<(int orderId, string status), TaskCompletionSource<bool>> _statusWaiters = new();

    public Task<int> NextValidIdAsync => _nextValidId.Task;
    public event Action<TradeTick>? OnTrade;
    public event Action<Candle>? OnRealtimeBar;

    public Task<int> ResolveConIdAsync(int reqId) =>
        _conIdByReq.GetOrAdd(reqId,
            _ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public Task WaitForStatusAsync(int orderId, string status) =>
        _statusWaiters.GetOrAdd((orderId, status),
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public override void connectAck() => Log.Line("connectAck — socket established");

    public override void nextValidId(int orderId)
    {
        Log.Line($"nextValidId = {orderId}");
        _nextValidId.TrySetResult(orderId);
    }

    public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        // 21xx are data-farm / connectivity status notices, not failures.
        var informational = errorCode is 2104 or 2106 or 2107 or 2108 or 2119 or 2158;
        Log.Line($"{(informational ? "info" : "ERROR")} id={id} code={errorCode} {errorMsg}");
    }

    public override void error(Exception e) => Log.Line($"EXCEPTION {e.Message}");
    public override void error(string str) => Log.Line($"ERROR {str}");

    public override void contractDetails(int reqId, ContractDetails details)
    {
        Log.Line($"contractDetails req={reqId} conId={details.Contract.ConId} {details.Contract.LocalSymbol}");
        if (_conIdByReq.TryGetValue(reqId, out var tcs)) tcs.TrySetResult(details.Contract.ConId);
    }

    public override void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size,
        TickAttribLast tickAttribLast, string exchange, string specialConditions)
        => OnTrade?.Invoke(new TradeTick(time * 1000L, price, size));

    public override void realtimeBar(int reqId, long date, double open, double high, double low, double close,
        decimal volume, decimal WAP, int count)
        => OnRealtimeBar?.Invoke(new Candle(date * 1000L, open, high, low, close, volume, count));

    public override void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
        => Log.Line($"openOrder id={orderId} {order.Action} {order.OrderType} qty={order.TotalQuantity} state={orderState.Status}");

    public override void orderStatus(int orderId, string status, decimal filled, decimal remaining,
        double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId,
        string whyHeld, double mktCapPrice)
    {
        Log.Line($"orderStatus id={orderId} status={status} filled={filled} avg={avgFillPrice}");
        if (_statusWaiters.TryGetValue((orderId, status), out var tcs)) tcs.TrySetResult(true);
    }

    public override void execDetails(int reqId, Contract contract, Execution execution)
        => Log.Line($"execDetails order={execution.OrderId} {execution.Side} {execution.Shares}@{execution.Price}");

    public override void commissionAndFeesReport(CommissionAndFeesReport report)
        => Log.Line($"commissionAndFeesReport exec={report.ExecId} commissionAndFees={report.CommissionAndFees}");

    public override void position(string account, Contract contract, decimal pos, double avgCost)
        => Log.Line($"position {contract.Symbol} qty={pos} avgCost={avgCost}");

    public override void positionEnd() => Log.Line("positionEnd");

    public override void accountSummary(int reqId, string account, string tag, string value, string currency)
        => Log.Line($"accountSummary {tag}={value} {currency}");

    public override void accountSummaryEnd(int reqId) => Log.Line("accountSummaryEnd");
}
