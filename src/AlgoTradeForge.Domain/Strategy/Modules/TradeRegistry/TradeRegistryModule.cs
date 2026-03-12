using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;

[ModuleKey("trade-registry")]
public sealed class TradeRegistryModule(TradeRegistryParams parameters) : IStrategyModule<TradeRegistryParams>, IEventBusReceiver
{
    private readonly TradeRegistryParams _params = parameters;
    private readonly Dictionary<long, OrderGroup> _groups = [];
    private readonly Dictionary<long, OrderGroup> _orderToGroup = [];
    private long _nextGroupId;
    private long _nextOrderId = -1_000_000; // Negative range to avoid collisions with engine-assigned IDs
    private IEventBus _bus = NullEventBus.Instance;
    private Func<DateTimeOffset> _clock = () => DateTimeOffset.UtcNow;

    public void SetEventBus(IEventBus bus) => _bus = bus;

    /// <summary>
    /// Override the clock used for timestamps. In backtests, set to simulation time
    /// (e.g., bar.Timestamp or fill.Timestamp) so events carry correct historical dates.
    /// </summary>
    public void SetClock(Func<DateTimeOffset> clock) => _clock = clock;

    // ── Queries ──────────────────────────────────────────────────

    public IEnumerable<OrderGroup> ActiveGroups =>
        _groups.Values.Where(g => g.Status is OrderGroupStatus.PendingEntry or OrderGroupStatus.ProtectionActive);

    public int ActiveGroupCount =>
        _groups.Values.Count(g => g.Status is OrderGroupStatus.PendingEntry or OrderGroupStatus.ProtectionActive);

    public bool IsFlat => ActiveGroupCount == 0;

    public OrderGroup? GetGroup(long groupId) =>
        _groups.GetValueOrDefault(groupId);

    // ── OpenGroup ────────────────────────────────────────────────

    public OrderGroup? OpenGroup(
        IOrderContext orders,
        Asset asset,
        OrderSide side,
        OrderType entryType,
        decimal quantity,
        long slPrice,
        ReadOnlySpan<TpLevel> tpLevels,
        long? entryLimitPrice = null,
        long? entryStopPrice = null,
        string? tag = null)
    {
        if (_params.MaxConcurrentGroups > 0 && ActiveGroupCount >= _params.MaxConcurrentGroups)
            return null;

        // TP closure < 100% is allowed; residual position is covered by SL only.
        // Caller is responsible for ensuring SL/TP prices are on the correct side
        // of the expected entry direction.
        var totalClosure = 0m;
        foreach (var tp in tpLevels)
            totalClosure += tp.ClosurePercentage;
        if (totalClosure > 1.0m)
            return null;

        // Module is single-threaded by design: backtest uses the engine loop,
        // live uses the per-session event queue for serialization.
        var groupId = ++_nextGroupId;
        var entryOrderId = --_nextOrderId;

        var group = new OrderGroup
        {
            GroupId = groupId,
            EntrySide = side,
            EntryQuantity = quantity,
            RemainingQuantity = quantity,
            SlPrice = slPrice,
            TpLevels = tpLevels.ToArray(),
            Asset = asset,
            CreatedAt = _clock(),
            Tag = tag,
            EntryOrderId = entryOrderId,
        };

        _groups[groupId] = group;
        _orderToGroup[entryOrderId] = group;

        var entryOrder = new Order
        {
            Id = entryOrderId,
            Asset = asset,
            Side = side,
            Type = entryType,
            Quantity = quantity,
            LimitPrice = entryLimitPrice,
            StopPrice = entryStopPrice,
            GroupId = groupId,
        };

        orders.Submit(entryOrder);

        EmitEvent(group, OrderGroupTransition.EntrySubmitted, entryOrderId, entryLimitPrice ?? entryStopPrice, quantity);

        return group;
    }

    // ── OnFill ───────────────────────────────────────────────────

    public void OnFill(Fill fill, Order order, IOrderContext orders)
    {
        if (!_orderToGroup.TryGetValue(fill.OrderId, out var group))
            return;

        if (fill.OrderId == group.EntryOrderId)
            HandleEntryFill(group, fill, orders);
        else if (fill.OrderId == group.SlOrderId)
            HandleSlFill(group, fill, orders);
        else if (fill.OrderId == group.LiquidationOrderId)
            HandleLiquidationFill(group, fill);
        else
            HandleTpFill(group, fill, orders);
    }

    private void HandleEntryFill(OrderGroup group, Fill fill, IOrderContext orders)
    {
        group.Status = OrderGroupStatus.ProtectionActive;
        group.EntryPrice = fill.Price;

        EmitEvent(group, OrderGroupTransition.EntryFilled, fill.OrderId, fill.Price, fill.Quantity);

        PlaceProtectiveOrders(group, orders);
    }

    private void HandleSlFill(OrderGroup group, Fill fill, IOrderContext orders)
    {
        ComputePnl(group, fill);
        group.RemainingQuantity = 0m;

        EmitEvent(group, OrderGroupTransition.SlFilled, fill.OrderId, fill.Price, fill.Quantity);

        // Cancel ALL pending TPs
        CancelAllPendingTps(group, orders);

        CloseGroup(group);
    }

    private void HandleTpFill(OrderGroup group, Fill fill, IOrderContext orders)
    {
        ComputePnl(group, fill);
        group.RemainingQuantity -= fill.Quantity;

        EmitEvent(group, OrderGroupTransition.TpFilled, fill.OrderId, fill.Price, fill.Quantity);

        // Remove filled TP from tracking
        _orderToGroup.Remove(fill.OrderId);
        group.FilledTpCount++;

        if (group.RemainingQuantity <= 0m || group.FilledTpCount >= group.TpLevels.Length)
        {
            // Fully closed — cancel SL + any remaining TPs
            CancelSl(group, orders);
            CancelAllPendingTps(group, orders);
            CloseGroup(group);
        }
        else
        {
            // Partially closed — replace SL with reduced qty (TPs already on exchange)
            ReplaceSl(group, orders);
        }
    }

    // ── Protective Orders ────────────────────────────────────────

    private void PlaceProtectiveOrders(OrderGroup group, IOrderContext orders)
    {
        ReplaceSl(group, orders);

        // TPs: Submit ALL levels upfront starting from FilledTpCount
        var closeSide = group.EntrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        for (var i = group.FilledTpCount; i < group.TpLevels.Length; i++)
        {
            var tp = group.TpLevels[i];
            var tpQuantity = group.EntryQuantity * tp.ClosurePercentage;
            SubmitTp(group, orders, i, closeSide, tp.Price, tpQuantity);
        }
    }

    // ── LiquidateGroup ───────────────────────────────────────────

    public bool LiquidateGroup(long groupId, IOrderContext orders)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return false;
        if (group.Status != OrderGroupStatus.ProtectionActive)
            return false;
        if (group.LiquidationOrderId != 0)
            return false;

        CancelSl(group, orders);
        CancelAllPendingTps(group, orders);

        var closeSide = group.EntrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var liqOrderId = --_nextOrderId;
        var liqOrder = new Order
        {
            Id = liqOrderId,
            Asset = group.Asset,
            Side = closeSide,
            Type = OrderType.Market,
            Quantity = group.RemainingQuantity,
            GroupId = group.GroupId,
        };

        group.LiquidationOrderId = liqOrderId;
        _orderToGroup[liqOrderId] = group;
        orders.Submit(liqOrder);

        EmitEvent(group, OrderGroupTransition.LiquidationSubmitted, liqOrderId, null, group.RemainingQuantity);

        return true;
    }

    private void HandleLiquidationFill(OrderGroup group, Fill fill)
    {
        ComputePnl(group, fill);
        group.RemainingQuantity = 0m;

        EmitEvent(group, OrderGroupTransition.LiquidationFilled, fill.OrderId, fill.Price, fill.Quantity);

        _orderToGroup.Remove(fill.OrderId);
        CloseGroup(group);
    }

    // ── CancelGroup ──────────────────────────────────────────────

    public bool CancelGroup(long groupId, IOrderContext orders)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return false;

        if (group.Status == OrderGroupStatus.PendingEntry)
        {
            orders.Cancel(group.EntryOrderId);
            group.Status = OrderGroupStatus.Cancelled;
            EmitEvent(group, OrderGroupTransition.EntryCancelled, group.EntryOrderId, null, null);
            return true;
        }

        if (group.Status == OrderGroupStatus.ProtectionActive)
        {
            CancelSl(group, orders);
            CancelAllPendingTps(group, orders);
            CloseGroup(group);
            return true;
        }

        return false;
    }

    // ── UpdateStopLoss ───────────────────────────────────────────

    public bool UpdateStopLoss(long groupId, long newSlPrice, IOrderContext orders)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return false;
        if (group.Status != OrderGroupStatus.ProtectionActive)
            return false;

        group.SlPrice = newSlPrice;
        ReplaceSl(group, orders);
        return true;
    }

    // ── CloseAllGroups ───────────────────────────────────────────

    /// <summary>
    /// Cancels PendingEntry groups and liquidates ProtectionActive groups.
    /// Liquidation submits a market close order, so the caller must ensure
    /// at least one more bar/tick is processed for the fill to arrive.
    /// </summary>
    public void CloseAllGroups(IOrderContext orders)
    {
        var activeGroups = _groups.Values
            .Where(g => g.Status is OrderGroupStatus.PendingEntry or OrderGroupStatus.ProtectionActive)
            .ToList();

        foreach (var group in activeGroups)
        {
            if (group.Status == OrderGroupStatus.ProtectionActive)
                LiquidateGroup(group.GroupId, orders);
            else
                CancelGroup(group.GroupId, orders);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void CancelSl(OrderGroup group, IOrderContext orders)
    {
        if (group.SlOrderId != 0)
        {
            orders.Cancel(group.SlOrderId);
            EmitEvent(group, OrderGroupTransition.ProtectiveCancelled, group.SlOrderId, null, null);
            _orderToGroup.Remove(group.SlOrderId);
            group.SlOrderId = 0;
        }
    }

    private void CancelAllPendingTps(OrderGroup group, IOrderContext orders)
    {
        for (var i = 0; i < group.TpLevels.Length; i++)
        {
            var tpOrderId = group.TpLevels[i].OrderId;
            if (tpOrderId != 0 && _orderToGroup.ContainsKey(tpOrderId))
            {
                orders.Cancel(tpOrderId);
                EmitEvent(group, OrderGroupTransition.ProtectiveCancelled, tpOrderId, null, null);
                _orderToGroup.Remove(tpOrderId);
            }
        }
    }

    private void ReplaceSl(OrderGroup group, IOrderContext orders)
    {
        CancelSl(group, orders);

        var closeSide = group.EntrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var slOrderId = --_nextOrderId;
        var slOrder = new Order
        {
            Id = slOrderId,
            Asset = group.Asset,
            Side = closeSide,
            Type = OrderType.Stop,
            Quantity = group.RemainingQuantity,
            StopPrice = group.SlPrice,
            GroupId = group.GroupId,
        };

        group.SlOrderId = slOrderId;
        _orderToGroup[slOrderId] = group;
        orders.Submit(slOrder);

        EmitEvent(group, OrderGroupTransition.SlPlaced, slOrderId, group.SlPrice, group.RemainingQuantity);
    }

    private void SubmitTp(OrderGroup group, IOrderContext orders, int tpIndex, OrderSide closeSide, long price, decimal quantity)
    {
        var tpOrderId = --_nextOrderId;
        var tpOrder = new Order
        {
            Id = tpOrderId,
            Asset = group.Asset,
            Side = closeSide,
            Type = OrderType.Limit,
            Quantity = quantity,
            LimitPrice = price,
            GroupId = group.GroupId,
        };

        group.TpLevels[tpIndex].OrderId = tpOrderId;
        _orderToGroup[tpOrderId] = group;
        orders.Submit(tpOrder);

        EmitEvent(group, OrderGroupTransition.TpPlaced, tpOrderId, price, quantity);
    }

    // ── Reconciliation ────────────────────────────────────────────

    public IReadOnlyList<ExpectedOrder> GetExpectedOrders()
    {
        var result = new List<ExpectedOrder>();
        foreach (var group in _groups.Values)
        {
            if (group.Status != OrderGroupStatus.ProtectionActive)
                continue;

            if (group.SlOrderId != 0)
            {
                result.Add(new ExpectedOrder(
                    group.SlOrderId, group.GroupId,
                    ExpectedOrderType.StopLoss, group.SlPrice, group.RemainingQuantity));
            }

            for (var i = 0; i < group.TpLevels.Length; i++)
            {
                var tp = group.TpLevels[i];
                if (tp.OrderId != 0 && _orderToGroup.ContainsKey(tp.OrderId))
                {
                    var tpQuantity = group.EntryQuantity * tp.ClosurePercentage;
                    result.Add(new ExpectedOrder(
                        tp.OrderId, group.GroupId,
                        ExpectedOrderType.TakeProfit, tp.Price, tpQuantity));
                }
            }

            if (group.LiquidationOrderId != 0)
            {
                result.Add(new ExpectedOrder(
                    group.LiquidationOrderId, group.GroupId,
                    ExpectedOrderType.Liquidation, 0L, group.RemainingQuantity));
            }
        }
        return result;
    }

    public void RepairGroup(long groupId, IReadOnlySet<long> missingOrderIds, IOrderContext orders)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return;

        if (group.Status != OrderGroupStatus.ProtectionActive)
            return;

        var closeSide = group.EntrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

        foreach (var missingId in missingOrderIds)
        {
            if (missingId == group.SlOrderId)
            {
                // SL is missing — remove old tracking and resubmit
                _orderToGroup.Remove(group.SlOrderId);
                group.SlOrderId = 0;
                ReplaceSl(group, orders);
            }
            else
            {
                // Check if it matches a TP level
                for (var i = 0; i < group.TpLevels.Length; i++)
                {
                    if (group.TpLevels[i].OrderId == missingId)
                    {
                        _orderToGroup.Remove(missingId);
                        group.TpLevels[i].OrderId = 0;
                        var tpQuantity = group.EntryQuantity * group.TpLevels[i].ClosurePercentage;
                        SubmitTp(group, orders, i, closeSide, group.TpLevels[i].Price, tpQuantity);
                        break;
                    }
                }
            }
        }
    }

    private static void ComputePnl(OrderGroup group, Fill fill)
    {
        var direction = group.EntrySide == OrderSide.Buy ? 1 : -1;
        group.RealizedPnl += MoneyConvert.ToLong(
            direction * (fill.Price - group.EntryPrice) * fill.Quantity * fill.Asset.Multiplier);
    }

    private void CloseGroup(OrderGroup group)
    {
        group.Status = OrderGroupStatus.Closed;
        group.ClosedAt = _clock();
    }

    private void EmitEvent(
        OrderGroup group,
        OrderGroupTransition transition,
        long? orderId,
        long? price,
        decimal? quantity)
    {
        _bus.Emit(new OrderGroupEvent(
            _clock(),
            EventSources.TradeRegistry,
            group.GroupId,
            group.Asset.Name,
            transition,
            orderId,
            price,
            quantity,
            group.Tag));
    }
}
