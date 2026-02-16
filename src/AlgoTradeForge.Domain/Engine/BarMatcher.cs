using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public class BarMatcher : IBarMatcher
{
    public decimal? GetFillPrice(Order order, Int64Bar bar, BacktestOptions options)
    {
        if (order.Type == OrderType.Market)
        {
            var direction = order.Side == OrderSide.Buy ? 1 : -1;
            return bar.Open + options.SlippageTicks * direction;
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

            if (order.Side == OrderSide.Buy)
            {
                // Stop Buy triggers when price rises to/above stop
                if (bar.Open >= stopPrice)
                    return bar.Open + options.SlippageTicks; // Gap up — fill at open + slippage

                if (bar.High >= stopPrice)
                    return stopPrice + options.SlippageTicks;
            }
            else
            {
                // Stop Sell triggers when price falls to/below stop
                if (bar.Open <= stopPrice)
                    return bar.Open - options.SlippageTicks; // Gap down — fill at open - slippage

                if (bar.Low <= stopPrice)
                    return stopPrice - options.SlippageTicks;
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

            // Stop triggered but limit not reached — engine handles marking as Triggered
            return null;
        }

        return null;
    }

    public SlTpMatchResult? EvaluateSlTp(
        Order originalOrder,
        decimal entryPrice,
        int nextTpIndex,
        Int64Bar bar,
        BacktestOptions options)
    {
        var slPrice = originalOrder.StopLossPrice;
        var tpLevels = originalOrder.TakeProfitLevels;

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
            return new SlTpMatchResult(slPrice!.Value, 1m, IsStopLoss: true, TpIndex: -1);

        if (slHit && !tpHit)
            return new SlTpMatchResult(slPrice!.Value, 1m, IsStopLoss: true, TpIndex: -1);

        // TP hit only
        return new SlTpMatchResult(tpPrice, tpClosurePercentage, IsStopLoss: false, TpIndex: tpIndex);
    }

    private static bool IsSlHit(OrderSide entrySide, decimal slPrice, Int64Bar bar) =>
        entrySide == OrderSide.Buy
            ? bar.Low <= slPrice   // Long position: SL is below — hit when price drops to SL
            : bar.High >= slPrice; // Short position: SL is above — hit when price rises to SL

    private static bool IsTpHit(OrderSide entrySide, decimal tpPrice, Int64Bar bar) =>
        entrySide == OrderSide.Buy
            ? bar.High >= tpPrice  // Long position: TP is above — hit when price rises to TP
            : bar.Low <= tpPrice;  // Short position: TP is below — hit when price drops to TP
}
