using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public interface IOrderValidator
{
    string? ValidateSubmission(Order order);
    string? ValidateSettlement(Order order, long fillPrice, Portfolio portfolio, BacktestOptions options);
}
