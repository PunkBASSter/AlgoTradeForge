namespace AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;

public interface IMoneyManagementModule : IStrategyModule
{
    decimal CalculateSize(long entryPrice, long stopLoss, StrategyContextBase context, Asset asset);
}
