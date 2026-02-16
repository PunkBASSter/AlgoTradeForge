using System.Diagnostics;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public sealed class BacktestEngine(IBarMatcher barMatcher)
{
    public BacktestResult Run(
        TimeSeries<Int64Bar>[] seriesPerSubscription,
        IInt64BarStrategy strategy,
        BacktestOptions options,
        CancellationToken ct = default)
    {
        var subscriptions = strategy.DataSubscriptions;

        if (seriesPerSubscription.Length != subscriptions.Count)
            throw new ArgumentException("Series array length must match strategy DataSubscriptions count.");

        var stopwatch = Stopwatch.StartNew();
        var portfolio = new Portfolio { InitialCash = options.InitialCash };
        portfolio.Initialize();

        var subCount = subscriptions.Count;
        var cursors = new int[subCount];
        var fills = new List<Fill>();
        var orderQueue = new OrderQueue();
        var orderContext = new BacktestOrderContext(orderQueue, fills);
        var orderIdCounter = 0L;
        var totalBarsDelivered = 0;
        var activeSlTpPositions = new List<ActiveSlTpPosition>();
        var lastPrices = new Dictionary<string, decimal>();
        var equityCurve = new List<decimal>();

        strategy.OnInit();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var minTimestampMs = long.MaxValue;
            var minSubIndex = -1;

            for (var s = 0; s < subCount; s++)
            {
                if (cursors[s] >= seriesPerSubscription[s].Count)
                    continue;

                var ts = seriesPerSubscription[s][cursors[s]].TimestampMs;
                if (ts < minTimestampMs)
                {
                    minTimestampMs = ts;
                    minSubIndex = s;
                }
            }

            if (minSubIndex == -1)
                break;

            // Deliver all bars at the same timestamp in subscription declaration order
            for (var s = minSubIndex; s < subCount; s++)
            {
                if (cursors[s] >= seriesPerSubscription[s].Count)
                    continue;

                var bar = seriesPerSubscription[s][cursors[s]];
                if (bar.TimestampMs != minTimestampMs)
                    continue;

                var subscription = subscriptions[s];
                var barTimestamp = bar.Timestamp;

                // Snapshot fill count so strategy can observe fills from this bar's processing
                orderContext.BeginBar(fills.Count);

                // Process pending orders for this asset against the new bar
                ProcessPendingOrders(subscription.Asset, bar, barTimestamp, options, orderQueue, fills, portfolio, activeSlTpPositions, strategy);

                // Evaluate SL/TP for active positions on this asset
                EvaluateSlTpPositions(subscription.Asset, bar, barTimestamp, options, fills, portfolio, activeSlTpPositions, strategy);

                // Deliver bar to strategy
                strategy.OnBar(bar, subscription, orderContext);
                AssignOrderIds(orderQueue, ref orderIdCounter, barTimestamp);

                lastPrices[subscription.Asset.Name] = (decimal)bar.Close;
                cursors[s]++;
                totalBarsDelivered++;
            }

            // Snapshot portfolio equity at this timestamp
            equityCurve.Add(portfolio.Equity(lastPrices));
        }

        stopwatch.Stop();
        return new BacktestResult(portfolio, fills, equityCurve, totalBarsDelivered, stopwatch.Elapsed);
    }

    private void ProcessPendingOrders(
        Asset asset,
        Int64Bar bar,
        DateTimeOffset timestamp,
        BacktestOptions options,
        OrderQueue queue,
        List<Fill> fills,
        Portfolio portfolio,
        List<ActiveSlTpPosition> activeSlTpPositions,
        IInt64BarStrategy strategy)
    {
        var pending = queue.GetPendingForAsset(asset);
        var toRemove = new List<long>();

        foreach (var order in pending)
        {
            var fill = barMatcher.TryFill(order, bar, options);
            if (fill is null)
                continue;

            var fillWithTimestamp = fill with { Timestamp = timestamp };

            // Check if strategy has enough cash for buy orders
            if (order.Side == OrderSide.Buy)
            {
                var cost = fillWithTimestamp.Price * fillWithTimestamp.Quantity * order.Asset.Multiplier
                           + fillWithTimestamp.Commission;
                if (cost > portfolio.Cash)
                {
                    order.Status = OrderStatus.Rejected;
                    toRemove.Add(order.Id);
                    continue;
                }
            }

            order.Status = OrderStatus.Filled;
            fills.Add(fillWithTimestamp);
            portfolio.Apply(fillWithTimestamp);
            strategy.OnTrade(fillWithTimestamp, order);
            toRemove.Add(order.Id);

            // Track SL/TP if the order has stop loss or take profit levels
            if (order.StopLossPrice.HasValue || order.TakeProfitLevels is { Count: > 0 })
            {
                activeSlTpPositions.Add(new ActiveSlTpPosition
                {
                    OriginalOrder = order,
                    EntryPrice = fillWithTimestamp.Price,
                    RemainingQuantity = fillWithTimestamp.Quantity,
                    NextTpIndex = 0
                });
            }
        }

        foreach (var id in toRemove)
            queue.Remove(id);
    }

    private void EvaluateSlTpPositions(
        Asset asset,
        Int64Bar bar,
        DateTimeOffset timestamp,
        BacktestOptions options,
        List<Fill> fills,
        Portfolio portfolio,
        List<ActiveSlTpPosition> activeSlTpPositions,
        IInt64BarStrategy strategy)
    {
        for (var i = activeSlTpPositions.Count - 1; i >= 0; i--)
        {
            var pos = activeSlTpPositions[i];
            if (pos.OriginalOrder.Asset != asset)
                continue;

            var slTpFill = barMatcher.EvaluateSlTp(
                pos.OriginalOrder,
                pos.EntryPrice,
                pos.RemainingQuantity,
                pos.NextTpIndex,
                bar,
                options,
                out var hitTpIndex);

            if (slTpFill is null)
                continue;

            var fillWithTimestamp = slTpFill with { Timestamp = timestamp };
            fills.Add(fillWithTimestamp);
            portfolio.Apply(fillWithTimestamp);
            strategy.OnTrade(fillWithTimestamp, pos.OriginalOrder);

            if (hitTpIndex < 0)
            {
                // SL closes entire position
                activeSlTpPositions.RemoveAt(i);
            }
            else
            {
                pos.RemainingQuantity -= slTpFill.Quantity;
                pos.NextTpIndex = hitTpIndex + 1;

                if (pos.RemainingQuantity <= 0 ||
                    pos.OriginalOrder.TakeProfitLevels is null ||
                    pos.NextTpIndex >= pos.OriginalOrder.TakeProfitLevels.Count)
                {
                    activeSlTpPositions.RemoveAt(i);
                }
            }
        }
    }

    private sealed class ActiveSlTpPosition
    {
        public required Order OriginalOrder { get; init; }
        public required decimal EntryPrice { get; init; }
        public decimal RemainingQuantity { get; set; }
        public int NextTpIndex { get; set; }
    }

    private static void AssignOrderIds(OrderQueue queue, ref long counter, DateTimeOffset timestamp)
    {
        foreach (var order in queue.GetAll())
        {
            if (order.Id == 0)
                order.Id = ++counter;

            if (order.SubmittedAt == default)
                order.SubmittedAt = timestamp;
        }
    }
}

internal sealed class BacktestOrderContext : IOrderContext
{
    private readonly OrderQueue _queue;
    private readonly List<Fill> _allFills;
    private int _fillSnapshotStart;
    private long _nextOrderId;

    public BacktestOrderContext(OrderQueue queue, List<Fill> allFills)
    {
        _queue = queue;
        _allFills = allFills;
    }

    public void BeginBar(int currentFillCount)
    {
        _fillSnapshotStart = currentFillCount;
    }

    public long Submit(Order order)
    {
        var id = ++_nextOrderId;
        // Create a new order with the assigned ID if the submitted one has Id=0
        // The caller should set the Id; if they used 'required', it's already set.
        _queue.Submit(order);
        return order.Id;
    }

    public bool Cancel(long orderId) => _queue.Cancel(orderId);

    public IReadOnlyList<Order> GetPendingOrders() => _queue.GetAll();

    public IReadOnlyList<Fill> GetFills()
    {
        if (_fillSnapshotStart >= _allFills.Count)
            return [];
        return _allFills.GetRange(_fillSnapshotStart, _allFills.Count - _fillSnapshotStart);
    }
}
