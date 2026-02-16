using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public interface IRiskEvaluator
{
    bool CanFill(Order order, decimal fillPrice, Portfolio portfolio, BacktestOptions options);
}

public class BasicRiskEvaluator : IRiskEvaluator
{
    public bool CanFill(Order order, decimal fillPrice, Portfolio portfolio, BacktestOptions options)
    {
        if (order.Side == OrderSide.Buy)
        {
            var cost = fillPrice * order.Quantity * order.Asset.Multiplier + options.CommissionPerTrade;
            return cost <= portfolio.Cash;
        }

        // Sell: no risk check — shorts are allowed, portfolio tracks negative positions
        return true;
    }
}
