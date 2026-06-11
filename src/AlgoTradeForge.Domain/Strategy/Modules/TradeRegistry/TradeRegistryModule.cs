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
    // Reference-keyed: OrderGroup is a class with no Equals override. Do NOT make it a record.
    private readonly HashSet<OrderGroup> _activeGroups = [];
    private readonly Dictionary<long, OrderGroup> _orderToGroup = [];
    private long _nextGroupId;
    private long _nextOrderId = -1_000_000; // Negative range to avoid collisions with engine-assigned IDs
    private IEventBus _bus = NullEventBus.Instance;
    private Func<DateTimeOffset> _clock = () => DateTimeOffset.UtcNow;
    private IOrderContext? _orders;

    public void SetEventBus(IEventBus bus) => _bus = bus;

    /// <summary>
    /// Override the clock used for timestamps. In backtests, set to simulation time
    /// (e.g., bar.Timestamp or fill.Timestamp) so events carry correct historical dates.
    /// </summary>
    public void SetClock(Func<DateTimeOffset> clock) => _clock = clock;

    /// <summary>
    /// Set the current order context. Called by the strategy at the start of each
    /// lifecycle method (OnBarStart, OnBarComplete, OnTrade) to scope the context
    /// for the duration of that callback.
    /// </summary>
    public void SetOrderContext(IOrderContext orders) => _orders = orders;

    private IOrderContext Orders => _orders
        ?? throw new InvalidOperationException("No order context set. Call SetOrderContext before invoking trade operations.");

    // ── Queries ──────────────────────────────────────────────────

    /// <summary>
    /// Live view over groups currently in <see cref="OrderGroupStatus.PendingEntry"/> or
    /// <see cref="OrderGroupStatus.ProtectionActive"/>. O(1) <c>Count</c>; zero-allocation
    /// enumeration via the underlying <see cref="HashSet{T}"/>'s struct enumerator. Mutating
    /// registry calls (OpenGroup, CancelGroup, fills) modify the underlying set, so callers
    /// iterating with intent to mutate must snapshot first (e.g. <c>.ToArray()</c>).
    /// Surface is intentionally <see cref="IReadOnlyCollection{T}"/> (not <c>IReadOnlySet</c>)
    /// to avoid implying stable membership semantics across mutations.
    /// </summary>
    public IReadOnlyCollection<OrderGroup> ActiveGroups => _activeGroups;

    public int ActiveGroupCount => _activeGroups.Count;

    public OrderGroup? GetGroup(long groupId) =>
        _groups.GetValueOrDefault(groupId);

    // ── OpenGroup ────────────────────────────────────────────────

    public OrderGroup? OpenGroup(
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
            EntryLimitPrice = entryLimitPrice,
            EntryStopPrice = entryStopPrice,
        };

        _groups[groupId] = group;
        _activeGroups.Add(group);
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

        Orders.Submit(entryOrder);

        EmitEvent(group, OrderGroupTransition.EntrySubmitted, entryOrderId, entryLimitPrice ?? entryStopPrice, quantity);

        return group;
    }

    // ── OnFill ───────────────────────────────────────────────────

    public void OnFill(Fill fill, Order order)
    {
        if (!_orderToGroup.TryGetValue(fill.OrderId, out var group))
            return;

        if (fill.OrderId == group.EntryOrderId)
            HandleEntryFill(group, fill);
        else if (fill.OrderId == group.SlOrderId)
            HandleSlFill(group, fill);
        else if (fill.OrderId == group.LiquidationOrderId)
            HandleLiquidationFill(group, fill);
        else
            HandleTpFill(group, fill);
    }

    private void HandleEntryFill(OrderGroup group, Fill fill)
    {
        // A fill can race a cancel (live: cancel sent, fill already in flight) or arrive
        // as a duplicate event. Processing it would resurrect a terminal group outside
        // _activeGroups and submit protective orders nothing tracks — orphans by construction.
        if (group.Status != OrderGroupStatus.PendingEntry)
            return;

        _orderToGroup.Remove(group.EntryOrderId);
        group.Status = OrderGroupStatus.ProtectionActive;
        group.EntryPrice = fill.Price;
        group.EntryFilledAt = _clock();

        EmitEvent(group, OrderGroupTransition.EntryFilled, fill.OrderId, fill.Price, fill.Quantity);

        PlaceProtectiveOrders(group);
    }

    private void HandleSlFill(OrderGroup group, Fill fill)
    {
        ComputePnl(group, fill);
        group.RemainingQuantity = 0m;

        EmitEvent(group, OrderGroupTransition.SlFilled, fill.OrderId, fill.Price, fill.Quantity);

        // The SL order is consumed by the fill — drop tracking before cancelling TPs
        _orderToGroup.Remove(fill.OrderId);
        group.SlOrderId = 0;

        // Cancel ALL pending TPs
        CancelAllPendingTps(group);

        CloseGroup(group);
    }

    private void HandleTpFill(OrderGroup group, Fill fill)
    {
        ComputePnl(group, fill);
        group.RemainingQuantity -= fill.Quantity;

        EmitEvent(group, OrderGroupTransition.TpFilled, fill.OrderId, fill.Price, fill.Quantity);

        // Remove filled TP from tracking
        _orderToGroup.Remove(fill.OrderId);
        for (var i = 0; i < group.TpLevels.Length; i++)
        {
            if (group.TpLevels[i].OrderId == fill.OrderId)
            {
                group.TpLevels[i].OrderId = 0;
                break;
            }
        }
        group.FilledTpCount++;

        if (group.RemainingQuantity <= 0m)
        {
            // Fully closed — cancel SL + any remaining TPs
            CancelSl(group);
            CancelAllPendingTps(group);
            CloseGroup(group);
        }
        else
        {
            // Residual remains (partial TP coverage or more TP levels outstanding) —
            // replace SL with reduced qty so the residual stays protected. The group
            // closes only when flat (SL/liquidation/remaining TPs); total TP closure
            // below 100% leaves the residual covered by SL per the OpenGroup contract.
            ReplaceSl(group);
        }
    }

    // ── Protective Orders ────────────────────────────────────────

    private void PlaceProtectiveOrders(OrderGroup group)
    {
        ReplaceSl(group);

        // TPs: Submit ALL levels upfront. Quantities are aligned to the asset's
        // QuantityStepSize; the last level gets the remainder to avoid residuals.
        var closeSide = group.EntrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var stepSize = group.Asset.QuantityStepSize;
        var allocated = 0m;

        for (var i = 0; i < group.TpLevels.Length; i++)
        {
            var tp = group.TpLevels[i];
            var tpQuantity = group.EntryQuantity * tp.ClosurePercentage;

            if (stepSize > 0m)
            {
                if (i == group.TpLevels.Length - 1 && i > 0)
                    tpQuantity = group.EntryQuantity - allocated;
                else
                    tpQuantity = Math.Floor(tpQuantity / stepSize) * stepSize;
            }

            allocated += tpQuantity;

            if (tpQuantity < group.Asset.MinOrderQuantity)
                continue;

            SubmitTp(group, i, closeSide, tp.Price, tpQuantity);
        }
    }

    // ── LiquidateGroup ───────────────────────────────────────────

    public bool LiquidateGroup(long groupId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return false;
        if (group.Status != OrderGroupStatus.ProtectionActive)
            return false;
        if (group.LiquidationOrderId != 0)
            return false;

        CancelSl(group);
        CancelAllPendingTps(group);

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
        Orders.Submit(liqOrder);

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

    /// <summary>
    /// PendingEntry: cancels the entry order and marks the group Cancelled.
    /// ProtectionActive: cancels the working SL/TP orders and marks the group Closed
    /// WITHOUT closing the position — the remaining position becomes the caller's
    /// responsibility (used by strategies that submit their own exit order). Use
    /// <see cref="LiquidateGroup"/> to flatten through the registry instead.
    /// Returns false while a liquidation is in flight: the group must stay alive
    /// to route the liquidation fill.
    /// </summary>
    public bool CancelGroup(long groupId)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return false;

        if (group.Status == OrderGroupStatus.PendingEntry)
        {
            Orders.Cancel(group.EntryOrderId);
            _orderToGroup.Remove(group.EntryOrderId);
            group.Status = OrderGroupStatus.Cancelled;
            _activeGroups.Remove(group);
            EmitEvent(group, OrderGroupTransition.EntryCancelled, group.EntryOrderId, null, null);
            return true;
        }

        if (group.Status == OrderGroupStatus.ProtectionActive)
        {
            if (group.LiquidationOrderId != 0)
                return false;

            CancelSl(group);
            CancelAllPendingTps(group);
            CloseGroup(group);
            return true;
        }

        return false;
    }

    // ── UpdateStopLoss ───────────────────────────────────────────

    public bool UpdateStopLoss(long groupId, long newSlPrice)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return false;
        if (group.Status != OrderGroupStatus.ProtectionActive)
            return false;

        group.SlPrice = newSlPrice;
        ReplaceSl(group);
        return true;
    }

    // ── CloseAllGroups ───────────────────────────────────────────

    /// <summary>
    /// Cancels PendingEntry groups and liquidates ProtectionActive groups.
    /// Liquidation submits a market close order, so the caller must ensure
    /// at least one more bar/tick is processed for the fill to arrive.
    /// </summary>
    public void CloseAllGroups()
    {
        var activeGroups = _groups.Values
            .Where(g => g.Status is OrderGroupStatus.PendingEntry or OrderGroupStatus.ProtectionActive)
            .ToList();

        foreach (var group in activeGroups)
        {
            if (group.Status == OrderGroupStatus.ProtectionActive)
                LiquidateGroup(group.GroupId);
            else
                CancelGroup(group.GroupId);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void CancelSl(OrderGroup group)
    {
        if (group.SlOrderId != 0)
        {
            Orders.Cancel(group.SlOrderId);
            EmitEvent(group, OrderGroupTransition.ProtectiveCancelled, group.SlOrderId, null, null);
            _orderToGroup.Remove(group.SlOrderId);
            group.SlOrderId = 0;
        }
    }

    private void CancelAllPendingTps(OrderGroup group)
    {
        for (var i = 0; i < group.TpLevels.Length; i++)
        {
            var tpOrderId = group.TpLevels[i].OrderId;
            if (tpOrderId != 0 && _orderToGroup.Remove(tpOrderId))
            {
                Orders.Cancel(tpOrderId);
                EmitEvent(group, OrderGroupTransition.ProtectiveCancelled, tpOrderId, null, null);
                group.TpLevels[i].OrderId = 0;
            }
        }
    }

    private void ReplaceSl(OrderGroup group)
    {
        CancelSl(group);

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
        Orders.Submit(slOrder);

        EmitEvent(group, OrderGroupTransition.SlPlaced, slOrderId, group.SlPrice, group.RemainingQuantity);
    }

    private void SubmitTp(OrderGroup group, int tpIndex, OrderSide closeSide, long price, decimal quantity)
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
        Orders.Submit(tpOrder);

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

    public void RepairGroup(long groupId, IReadOnlySet<long> missingOrderIds)
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
                ReplaceSl(group);
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
                        SubmitTp(group, i, closeSide, group.TpLevels[i].Price, tpQuantity);
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
        _activeGroups.Remove(group);
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
