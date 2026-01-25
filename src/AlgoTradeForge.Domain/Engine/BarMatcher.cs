using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public class BarMatcher : IBarMatcher
{
    public virtual Fill? TryFill(Order order, OhlcvBar bar, BacktestOptions options)
    {
        var fillPrice = GetFillPrice(order, bar, options);
        if (fillPrice is null)
            return null;

        return new Fill(
            order.Id,
            order.Asset,
            bar.Timestamp,
            fillPrice.Value,
            order.Quantity,
            order.Side,
            options.CommissionPerTrade);
    }

    protected virtual decimal? GetFillPrice(Order order, OhlcvBar bar, BacktestOptions options)
    {
        if (order.Type == OrderType.Market)
        {
            var slippage = options.SlippageTicks * order.Asset.TickSize;
            var direction = order.Side == OrderSide.Buy ? 1 : -1;
            return bar.Open + slippage * direction;
        }

        if (order.Type == OrderType.Limit)
        {
            if (order.LimitPrice is not { } limitPrice)
                return null;

            if (order.Side == OrderSide.Buy && limitPrice >= bar.Low)
                return limitPrice;

            if (order.Side == OrderSide.Sell && limitPrice <= bar.High)
                return limitPrice;

            return null;
        }

        return null;
    }
}
