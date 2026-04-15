namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

public interface IMoneyManagementModule : IStrategyModule
{
    decimal CalculateSize(long entryPrice, long stopLoss, StrategyContext context, Asset asset);
}
