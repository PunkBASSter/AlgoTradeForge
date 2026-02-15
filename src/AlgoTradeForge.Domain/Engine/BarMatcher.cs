using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public class BarMatcher : IBarMatcher
{
    public virtual Fill? TryFill(Order order, Int64Bar bar, BacktestOptions options)
    {
        var fillPrice = GetFillPrice(order, bar, options);
        if (fillPrice is null)
            return null;

        return new Fill(
            order.Id,
            order.Asset,
            new DateTimeOffset(), //bar.Timestamp,
            fillPrice.Value,
            order.Quantity,
            order.Side,
            options.CommissionPerTrade);
    }

    protected virtual decimal? GetFillPrice(Order order, Int64Bar bar, BacktestOptions options)
    {
        if (order.Type == OrderType.Market)
        {
            var slippage = options.SlippageTicks * order.Asset.TickSize;
            var direction = order.Side == OrderSide.Buy ? 1 : -1;
            return bar.Open + slippage * direction;
        }

        if (order.Type == OrderType.Limit || (order.Type == OrderType.StopLimit && order.Status == OrderStatus.Triggered))
        {
            if (order.LimitPrice is not { } limitPrice)
                return null;

            if (order.Side == OrderSide.Buy && limitPrice >= bar.Low)
                return limitPrice;

            if (order.Side == OrderSide.Sell && limitPrice <= bar.High)
                return limitPrice;

            return null;
        }

        if (order.Type == OrderType.Stop)
        {
            if (order.StopPrice is not { } stopPrice)
                return null;

            var slippage = options.SlippageTicks * order.Asset.TickSize;

            if (order.Side == OrderSide.Buy)
            {
                // Stop Buy triggers when price rises to/above stop
                if (bar.Open >= stopPrice)
                    return bar.Open + slippage; // Gap up — fill at open + slippage

                if (bar.High >= stopPrice)
                    return stopPrice + slippage;
            }
            else
            {
                // Stop Sell triggers when price falls to/below stop
                if (bar.Open <= stopPrice)
                    return bar.Open - slippage; // Gap down — fill at open - slippage

                if (bar.Low <= stopPrice)
                    return stopPrice - slippage;
            }

            return null;
        }

        if (order.Type == OrderType.StopLimit)
        {
            if (order.StopPrice is not { } stopPrice || order.LimitPrice is not { } limitPrice)
                return null;

            // Check if stop is triggered on this bar
            var triggered = order.Side == OrderSide.Buy
                ? bar.Open >= stopPrice || bar.High >= stopPrice
                : bar.Open <= stopPrice || bar.Low <= stopPrice;

            if (!triggered)
                return null;

            // Stop triggered — now check if limit can be filled on same bar
            if (order.Side == OrderSide.Buy && limitPrice >= bar.Low)
                return limitPrice;

            if (order.Side == OrderSide.Sell && limitPrice <= bar.High)
                return limitPrice;

            // Stop triggered but limit not reached — mark as Triggered for future bars
            order.Status = OrderStatus.Triggered;
            return null;
        }

        return null;
    }

    public virtual Fill? EvaluateSlTp(
        Order originalOrder,
        decimal entryPrice,
        decimal remainingQuantity,
        int nextTpIndex,
        Int64Bar bar,
        BacktestOptions options,
        out int hitTpIndex)
    {
        hitTpIndex = -1;

        var slPrice = originalOrder.StopLossPrice;
        var tpLevels = originalOrder.TakeProfitLevels;
        var closeSide = originalOrder.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

        var slHit = slPrice.HasValue && IsSlHit(originalOrder.Side, slPrice.Value, bar);

        // Find first reachable TP
        var tpHit = false;
        var tpPrice = 0m;
        var tpIndex = -1;
        var tpClosurePercentage = 1m;

        if (tpLevels is { Count: > 0 } && nextTpIndex < tpLevels.Count)
        {
            var tp = tpLevels[nextTpIndex];
            tpHit = IsTpHit(originalOrder.Side, tp.Price, bar);
            if (tpHit)
            {
                tpPrice = tp.Price;
                tpIndex = nextTpIndex;
                tpClosurePercentage = tp.ClosurePercentage;
            }
        }

        if (!slHit && !tpHit)
            return null;

        // Both hit — worst case: SL wins (unless UseDetailedExecutionLogic, handled by engine)
        if (slHit && tpHit && !options.UseDetailedExecutionLogic)
        {
            return CreateSlFill(originalOrder, closeSide, slPrice!.Value, remainingQuantity, options);
        }

        if (slHit && !tpHit)
        {
            return CreateSlFill(originalOrder, closeSide, slPrice!.Value, remainingQuantity, options);
        }

        // TP hit only
        hitTpIndex = tpIndex;
        var tpQuantity = remainingQuantity * tpClosurePercentage;
        return new Fill(
            originalOrder.Id,
            originalOrder.Asset,
            default,
            tpPrice,
            tpQuantity,
            closeSide,
            options.CommissionPerTrade);
    }

    private static Fill CreateSlFill(Order order, OrderSide closeSide, decimal slPrice, decimal quantity, BacktestOptions options) =>
        new(order.Id, order.Asset, default, slPrice, quantity, closeSide, options.CommissionPerTrade);

    private static bool IsSlHit(OrderSide entrySide, decimal slPrice, Int64Bar bar) =>
        entrySide == OrderSide.Buy
            ? bar.Low <= slPrice   // Long position: SL is below — hit when price drops to SL
            : bar.High >= slPrice; // Short position: SL is above — hit when price rises to SL

    private static bool IsTpHit(OrderSide entrySide, decimal tpPrice, Int64Bar bar) =>
        entrySide == OrderSide.Buy
            ? bar.High >= tpPrice  // Long position: TP is above — hit when price rises to TP
            : bar.Low <= tpPrice;  // Short position: TP is below — hit when price drops to TP
}
