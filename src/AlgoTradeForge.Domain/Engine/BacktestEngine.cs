using System.Diagnostics;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

/// <summary>
/// Stateless backtest engine. Safe for concurrent use — all mutable state is local to <see cref="Run"/>.
/// </summary>
public sealed class BacktestEngine(IBarMatcher barMatcher, IRiskEvaluator riskEvaluator)
{
    private const string Source = EventSources.Engine;

    public BacktestResult Run(
        TimeSeries<Int64Bar>[] seriesPerSubscription,
        IInt64BarStrategy strategy,
        BacktestOptions options,
        CancellationToken ct = default,
        IDebugProbe? probe = null,
        IEventBus? bus = null,
        Action<int>? onBarsProcessed = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var state = InitializeRun(seriesPerSubscription, strategy, options, probe, bus);
        state.OnBarsProcessed = onBarsProcessed;

        try
        {
            RunMainLoop(state, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitError(state, ex);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            FinalizeRun(state, stopwatch);
        }

        return new BacktestResult(state.Portfolio, state.Fills, state.EquityCurve, state.TotalBarsDelivered, stopwatch.Elapsed);
    }

    private static RunState InitializeRun(
        TimeSeries<Int64Bar>[] series,
        IInt64BarStrategy strategy,
        BacktestOptions options,
        IDebugProbe? probe,
        IEventBus? bus)
    {
        probe ??= NullDebugProbe.Instance;
        bus ??= NullEventBus.Instance;
        var subscriptions = strategy.DataSubscriptions;

        if (series.Length != subscriptions.Count)
            throw new ArgumentException("Series array length must match strategy DataSubscriptions count.");

        var fills = new List<Fill>();
        var orderQueue = new OrderQueue();
        var portfolio = new Portfolio { InitialCash = options.InitialCash };
        portfolio.Initialize();

        var state = new RunState
        {
            Probe = probe,
            Bus = bus,
            ProbeActive = probe.IsActive,
            BusActive = bus is not NullEventBus,
            Strategy = strategy,
            Options = options,
            Subscriptions = subscriptions,
            Series = series,
            Portfolio = portfolio,
            OrderContext = new BacktestOrderContext(orderQueue, fills, portfolio, bus),
            OrderQueue = orderQueue,
            Fills = fills,
            Cursors = new int[subscriptions.Count],
        };

        if (strategy is IEventBusReceiver receiver)
            receiver.SetEventBus(bus);

        strategy.OnInit();

        if (state.BusActive)
        {
            bus.Emit(new RunStartEvent(
                DateTimeOffset.UtcNow,
                Source,
                strategy.GetType().Name,
                options.Asset.Name,
                options.InitialCash,
                options.StartTime,
                options.EndTime,
                ExportMode.Backtest));
        }

        if (state.ProbeActive)
            probe.OnRunStart();

        return state;
    }

    private void RunMainLoop(RunState state, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var (minSubIndex, minTimestampMs) = FindNextTimestamp(state);
            if (minSubIndex == -1)
                break;

            DeliverBarsAtTimestamp(state, minSubIndex, minTimestampMs);

            // Snapshot portfolio equity at this timestamp
            state.EquityCurve.Add(new EquitySnapshot(minTimestampMs, state.Portfolio.Equity(state.LastPrices)));
        }
    }

    private static (int subIndex, long timestampMs) FindNextTimestamp(RunState state)
    {
        var minTimestampMs = long.MaxValue;
        var minSubIndex = -1;
        var subCount = state.Subscriptions.Count;

        for (var s = 0; s < subCount; s++)
        {
            if (state.Cursors[s] >= state.Series[s].Count)
                continue;

            var ts = state.Series[s][state.Cursors[s]].TimestampMs;
            if (ts < minTimestampMs)
            {
                minTimestampMs = ts;
                minSubIndex = s;
            }
        }

        return (minSubIndex, minTimestampMs);
    }

    private void DeliverBarsAtTimestamp(RunState state, int minSubIndex, long timestampMs)
    {
        var subCount = state.Subscriptions.Count;

        for (var s = minSubIndex; s < subCount; s++)
        {
            if (state.Cursors[s] >= state.Series[s].Count)
                continue;

            var bar = state.Series[s][state.Cursors[s]];
            if (bar.TimestampMs != timestampMs)
                continue;

            var subscription = state.Subscriptions[s];
            var barTimestamp = bar.Timestamp;

            // Snapshot fill count so strategy can observe fills from this bar's processing
            var fillCountBefore = state.Fills.Count;
            state.OrderContext.BeginBar(fillCountBefore, barTimestamp);

            // Notify strategy that a new bar is starting (open price only)
            var startBar = new Int64Bar(bar.TimestampMs, bar.Open, bar.Open, bar.Open, bar.Open, 0);
            state.Strategy.OnBarStart(startBar, subscription, state.OrderContext);
            AssignOrderIds(state, barTimestamp);

            // Process pending orders for this asset against the new bar
            ProcessPendingOrders(state, subscription.Asset, bar, barTimestamp);

            // Evaluate SL/TP for active positions on this asset
            EvaluateSlTpPositions(state, subscription.Asset, bar, barTimestamp);

            // Deliver completed bar to strategy
            state.Strategy.OnBarComplete(bar, subscription, state.OrderContext);
            AssignOrderIds(state, barTimestamp);

            state.LastPrices[subscription.Asset.Name] = bar.Close;
            state.Cursors[s]++;
            state.TotalBarsDelivered++;
            state.OnBarsProcessed?.Invoke(state.TotalBarsDelivered);

            EmitBar(state, bar, subscription);

            if (state.ProbeActive)
            {
                var fillsThisBar = state.Fills.Count - fillCountBefore;
                state.Probe.OnBarProcessed(new DebugSnapshot(
                    ++state.SequenceNumber,
                    bar.TimestampMs,
                    s,
                    subscription.IsExportable,
                    fillsThisBar,
                    state.Portfolio.Equity(state.LastPrices)));
            }
        }
    }

    private static void EmitError(RunState state, Exception ex)
    {
        if (state.BusActive)
            try { state.Bus.Emit(new ErrorEvent(DateTimeOffset.UtcNow, Source, ex.Message, ex.StackTrace)); }
            catch { /* Don't mask the original exception */ }
    }

    private static void FinalizeRun(RunState state, Stopwatch stopwatch)
    {
        if (state.BusActive)
            try
            {
                state.Bus.Emit(new RunEndEvent(
                    DateTimeOffset.UtcNow,
                    Source,
                    state.TotalBarsDelivered,
                    state.EquityCurve.Count > 0 ? state.EquityCurve[^1].Value : state.Options.InitialCash,
                    state.Fills.Count,
                    stopwatch.Elapsed));
            }
            catch { /* Don't mask the original exception */ }
        if (state.ProbeActive)
            try { state.Probe.OnRunEnd(); }
            catch { /* Don't mask the original exception */ }
    }

    private void ProcessPendingOrders(RunState state, Asset asset, Int64Bar bar, DateTimeOffset timestamp)
    {
        var pending = state.OrderQueue.GetPendingForAsset(asset);
        state.ToRemoveBuffer.Clear();

        foreach (var order in pending)
        {
            var fillPrice = barMatcher.GetFillPrice(order, bar, state.Options);
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

            var riskPassed = riskEvaluator.CanFill(order, fillPrice.Value, state.Portfolio, state.Options);
            EmitRiskCheck(state, order, riskPassed);

            if (!riskPassed)
            {
                order.Status = OrderStatus.Rejected;
                state.ToRemoveBuffer.Add(order.Id);
                EmitOrderRejected(state, order);
                continue;
            }

            var fill = new Fill(
                order.Id,
                order.Asset,
                timestamp,
                fillPrice.Value,
                order.Quantity,
                order.Side,
                state.Options.CommissionPerTrade);

            order.Status = OrderStatus.Filled;
            state.Fills.Add(fill);
            state.Portfolio.Apply(fill);
            state.Strategy.OnTrade(fill, order);
            state.ToRemoveBuffer.Add(order.Id);

            EmitFillAndPosition(state, timestamp, fill);

            // Track SL/TP if the order has stop loss or take profit levels
            if (order.StopLossPrice.HasValue || order.TakeProfitLevels is { Count: > 0 })
            {
                state.ActiveSlTpPositions.Add(new RunState.ActiveSlTpPosition
                {
                    OriginalOrder = order,
                    EntryPrice = fill.Price,
                    RemainingQuantity = fill.Quantity,
                    NextTpIndex = 0
                });
            }
        }

        foreach (var id in state.ToRemoveBuffer)
            state.OrderQueue.Remove(id);
    }

    private void EvaluateSlTpPositions(RunState state, Asset asset, Int64Bar bar, DateTimeOffset timestamp)
    {
        for (var i = state.ActiveSlTpPositions.Count - 1; i >= 0; i--)
        {
            var pos = state.ActiveSlTpPositions[i];
            if (pos.OriginalOrder.Asset != asset)
                continue;

            var result = barMatcher.EvaluateSlTp(
                pos.OriginalOrder,
                pos.EntryPrice,
                pos.NextTpIndex,
                bar,
                state.Options);

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
                state.Options.CommissionPerTrade);

            state.Fills.Add(fill);
            state.Portfolio.Apply(fill);
            state.Strategy.OnTrade(fill, pos.OriginalOrder);

            EmitFillAndPosition(state, timestamp, fill);

            if (match.IsStopLoss)
            {
                // SL closes entire position
                state.ActiveSlTpPositions.RemoveAt(i);
            }
            else
            {
                pos.RemainingQuantity -= quantity;
                pos.NextTpIndex = match.TpIndex + 1;

                if (pos.RemainingQuantity <= 0 ||
                    pos.OriginalOrder.TakeProfitLevels is null ||
                    pos.NextTpIndex >= pos.OriginalOrder.TakeProfitLevels.Count)
                {
                    state.ActiveSlTpPositions.RemoveAt(i);
                }
            }
        }
    }

    private static void EmitBar(RunState state, Int64Bar bar, DataSubscription subscription)
    {
        if (!state.BusActive)
            return;

        state.Bus.Emit(new BarEvent(
            bar.Timestamp,
            Source,
            subscription.Asset.Name,
            TimeFrameFormatter.Format(subscription.TimeFrame),
            bar.Open,
            bar.High,
            bar.Low,
            bar.Close,
            bar.Volume,
            subscription.IsExportable));
    }

    private static void EmitRiskCheck(RunState state, Order order, bool passed)
    {
        if (!state.BusActive)
            return;

        state.Bus.Emit(new RiskEvent(
            DateTimeOffset.UtcNow,
            Source,
            order.Asset.Name,
            passed,
            "CashCheck",
            passed ? null : "Insufficient cash"));
    }

    private static void EmitOrderRejected(RunState state, Order order)
    {
        if (!state.BusActive)
            return;

        state.Bus.Emit(new OrderRejectEvent(
            DateTimeOffset.UtcNow,
            Source,
            order.Id,
            order.Asset.Name,
            "Insufficient cash"));

        state.Bus.Emit(new WarningEvent(
            DateTimeOffset.UtcNow,
            Source,
            $"Order {order.Id} rejected: insufficient cash for {order.Side} {order.Quantity} {order.Asset.Name}"));
    }

    private static void EmitFillAndPosition(RunState state, DateTimeOffset timestamp, Fill fill)
    {
        if (!state.BusActive)
            return;

        state.Bus.Emit(new OrderFillEvent(
            timestamp,
            Source,
            fill.OrderId,
            fill.Asset.Name,
            fill.Side,
            fill.Price,
            fill.Quantity,
            fill.Commission));

        var position = state.Portfolio.GetPosition(fill.Asset.Name);
        if (position is not null)
        {
            state.Bus.Emit(new PositionEvent(
                timestamp,
                Source,
                fill.Asset.Name,
                position.Quantity,
                position.AverageEntryPrice,
                position.RealizedPnl));
        }
    }

    private static void AssignOrderIds(RunState state, DateTimeOffset timestamp)
    {
        foreach (var order in state.OrderQueue.GetAll())
        {
            if (order.Id == 0)
                order.Id = ++state.OrderIdCounter;

            if (order.SubmittedAt == default)
            {
                order.SubmittedAt = timestamp;

                if (state.BusActive)
                {
                    state.Bus.Emit(new OrderPlaceEvent(
                        timestamp,
                        Source,
                        order.Id,
                        order.Asset.Name,
                        order.Side,
                        order.Type,
                        order.Quantity,
                        order.LimitPrice,
                        order.StopPrice));
                }
            }
        }
    }

    private sealed class RunState
    {
        // Per-run dependencies (set once at init)
        public required IDebugProbe Probe;
        public required IEventBus Bus;
        public required bool ProbeActive;
        public required bool BusActive;
        public required IInt64BarStrategy Strategy;
        public required BacktestOptions Options;
        public required IList<DataSubscription> Subscriptions;
        public required TimeSeries<Int64Bar>[] Series;

        // Mutable simulation state
        public required Portfolio Portfolio;
        public required BacktestOrderContext OrderContext;
        public required OrderQueue OrderQueue;
        public required List<Fill> Fills;
        public readonly List<ActiveSlTpPosition> ActiveSlTpPositions = [];
        public readonly Dictionary<string, long> LastPrices = [];
        public readonly List<EquitySnapshot> EquityCurve = [];
        public required int[] Cursors;
        public readonly List<long> ToRemoveBuffer = [];
        public long OrderIdCounter;
        public long SequenceNumber;
        public int TotalBarsDelivered;
        public Action<int>? OnBarsProcessed;

        public sealed class ActiveSlTpPosition
        {
            public required Order OriginalOrder { get; init; }
            public required long EntryPrice { get; init; }
            public decimal RemainingQuantity { get; set; }
            public int NextTpIndex { get; set; }
        }
    }
}
