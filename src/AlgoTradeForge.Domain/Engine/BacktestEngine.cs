using System.Diagnostics;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

/// <summary>
/// Stateless backtest engine. Safe for concurrent use — all mutable state is local to <see cref="Run"/>.
/// </summary>
public sealed class BacktestEngine(IBarMatcher barMatcher, IRiskEvaluator riskEvaluator)
{
    public BacktestResult Run(
        TimeSeries<Int64Bar>[] seriesPerSubscription,
        IInt64BarStrategy strategy,
        BacktestOptions options,
        CancellationToken ct = default,
        IDebugProbe? probe = null)
    {
        probe ??= NullDebugProbe.Instance;
        var probeActive = probe.IsActive;
        var sequenceNumber = 0L;
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
        var orderContext = new BacktestOrderContext(orderQueue, fills, portfolio);
        var orderIdCounter = 0L;
        var totalBarsDelivered = 0;
        var activeSlTpPositions = new List<ActiveSlTpPosition>();
        var lastPrices = new Dictionary<string, long>();
        var equityCurve = new List<long>();

        strategy.OnInit();

        if (probeActive)
            probe.OnRunStart();

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
                var fillCountBefore = fills.Count;
                orderContext.BeginBar(fillCountBefore);

                // Notify strategy that a new bar is starting (open price only)
                var startBar = new Int64Bar(bar.TimestampMs, bar.Open, bar.Open, bar.Open, bar.Open, 0);
                strategy.OnBarStart(startBar, subscription, orderContext);
                AssignOrderIds(orderQueue, ref orderIdCounter, barTimestamp);

                // Process pending orders for this asset against the new bar
                ProcessPendingOrders(subscription.Asset, bar, barTimestamp, options, orderQueue, fills, portfolio, activeSlTpPositions, strategy);

                // Evaluate SL/TP for active positions on this asset
                EvaluateSlTpPositions(subscription.Asset, bar, barTimestamp, options, fills, portfolio, activeSlTpPositions, strategy);

                // Deliver completed bar to strategy
                strategy.OnBarComplete(bar, subscription, orderContext);
                AssignOrderIds(orderQueue, ref orderIdCounter, barTimestamp);

                lastPrices[subscription.Asset.Name] = bar.Close;
                cursors[s]++;
                totalBarsDelivered++;

                if (probeActive)
                {
                    var fillsThisBar = fills.Count - fillCountBefore;
                    probe.OnBarProcessed(new DebugSnapshot(
                        ++sequenceNumber,
                        bar.TimestampMs,
                        s,
                        subscription.IsExportable,
                        fillsThisBar,
                        portfolio.Equity(lastPrices)));
                }
            }

            // Snapshot portfolio equity at this timestamp
            equityCurve.Add(portfolio.Equity(lastPrices));
        }

        if (probeActive)
            probe.OnRunEnd();

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
            var fillPrice = barMatcher.GetFillPrice(order, bar, options);
            if (fillPrice is null)
            {
                // StopLimit: stop was triggered but limit not reached — mark as Triggered
                if (order.Type == OrderType.StopLimit && order.Status == OrderStatus.Pending && order.StopPrice is { } stopPrice)
                {
                    var stopTriggered = order.Side == OrderSide.Buy
                        ? bar.Open >= stopPrice || bar.High >= stopPrice
                        : bar.Open <= stopPrice || bar.Low <= stopPrice;

                    if (stopTriggered)
                        order.Status = OrderStatus.Triggered;
                }

                continue;
            }

            if (!riskEvaluator.CanFill(order, fillPrice.Value, portfolio, options))
            {
                order.Status = OrderStatus.Rejected;
                toRemove.Add(order.Id);
                continue;
            }

            var fill = new Fill(
                order.Id,
                order.Asset,
                timestamp,
                fillPrice.Value,
                order.Quantity,
                order.Side,
                options.CommissionPerTrade);

            order.Status = OrderStatus.Filled;
            fills.Add(fill);
            portfolio.Apply(fill);
            strategy.OnTrade(fill, order);
            toRemove.Add(order.Id);

            // Track SL/TP if the order has stop loss or take profit levels
            if (order.StopLossPrice.HasValue || order.TakeProfitLevels is { Count: > 0 })
            {
                activeSlTpPositions.Add(new ActiveSlTpPosition
                {
                    OriginalOrder = order,
                    EntryPrice = fill.Price,
                    RemainingQuantity = fill.Quantity,
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

            var result = barMatcher.EvaluateSlTp(
                pos.OriginalOrder,
                pos.EntryPrice,
                pos.NextTpIndex,
                bar,
                options);

            if (result is not { } match)
                continue;

            var closeSide = pos.OriginalOrder.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            var quantity = match.IsStopLoss
                ? pos.RemainingQuantity
                : pos.RemainingQuantity * match.ClosurePercentage;

            var fill = new Fill(
                pos.OriginalOrder.Id,
                pos.OriginalOrder.Asset,
                timestamp,
                match.Price,
                quantity,
                closeSide,
                options.CommissionPerTrade);

            fills.Add(fill);
            portfolio.Apply(fill);
            strategy.OnTrade(fill, pos.OriginalOrder);

            if (match.IsStopLoss)
            {
                // SL closes entire position
                activeSlTpPositions.RemoveAt(i);
            }
            else
            {
                pos.RemainingQuantity -= quantity;
                pos.NextTpIndex = match.TpIndex + 1;

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
        public required long EntryPrice { get; init; }
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
