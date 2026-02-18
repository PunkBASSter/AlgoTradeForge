using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.ZigZagBreakout;

[StrategyKey("ZigZagBreakout")]
public sealed class ZigZagBreakoutStrategy(ZigZagBreakoutParams parameters) : StrategyBase<ZigZagBreakoutParams>(parameters)
{
    private DeltaZigZag _dzz = null!;
    private readonly List<Int64Bar> _barHistory = [];

    private long? _pendingOrderId;
    private bool _isInPosition;
    private long _nextOrderId = 1; // Start at 1 so engine's AssignOrderIds (Id==0 check) won't overwrite

    public override void OnInit()
    {
        _dzz = new DeltaZigZag(Params.DzzDepth / 10m, Params.MinimumThreshold);
    }

    public override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
    {
        _barHistory.Add(bar);
        _dzz.Compute(_barHistory);

        if (_isInPosition)
            return;

        var values = _dzz.Buffers["Value"];
        var recentStart = Math.Max(0, values.Count - 20);
        var pivots = new List<long>();
        for (var i = recentStart; i < values.Count; i++)
        {
            if (values[i] != 0L)
                pivots.Add(values[i]);
        }

        if (pivots.Count < 3)
        {
            CancelPending(orders);
            return;
        }

        var sl = pivots[^3];
        var price = pivots[^2];
        var l1 = pivots[^1];

        // Bullish breakout pattern: price > sl (higher high) and l1 < price (higher low pullback)
        if (price > sl && l1 < price)
        {
            var tp = price + Math.Abs(price - sl);
            var slDistance = Math.Abs(price - sl);
            var positionSize = slDistance > 0
                ? Math.Clamp(orders.Cash * (Params.RiskPercentPerTrade / 100m) / slDistance, Params.MinPositionSize, Params.MaxPositionSize)
                : Params.MinPositionSize;

            // Check if existing pending order already matches this signal
            if (_pendingOrderId.HasValue)
            {
                var pending = orders.GetPendingOrders();
                var existing = pending.FirstOrDefault(o => o.Id == _pendingOrderId.Value);
                if (existing is not null && existing.StopPrice == (decimal)price && existing.StopLossPrice == (decimal)sl)
                    return; // Same signal, keep existing order

                // Different signal — cancel old, submit new
                orders.Cancel(_pendingOrderId.Value);
                _pendingOrderId = null;
            }

            var orderId = _nextOrderId++;
            var asset = subscription.Asset;
            orders.Submit(new Order
            {
                Id = orderId,
                Asset = asset,
                Side = OrderSide.Buy,
                Type = OrderType.Stop,
                Quantity = positionSize,
                StopPrice = price,
                StopLossPrice = sl,
                TakeProfitLevels = [new TakeProfitLevel(tp, 1m)]
            });
            _pendingOrderId = orderId;
        }
        else
        {
            CancelPending(orders);
        }
    }

    public override void OnTrade(Fill fill, Order order)
    {
        if (fill.Side == order.Side)
        {
            // Entry fill
            _isInPosition = true;
            _pendingOrderId = null;
        }
        else
        {
            // Exit fill (SL or TP)
            _isInPosition = false;
        }
    }

    private void CancelPending(IOrderContext orders)
    {
        if (!_pendingOrderId.HasValue) return;
        orders.Cancel(_pendingOrderId.Value);
        _pendingOrderId = null;
    }
}
