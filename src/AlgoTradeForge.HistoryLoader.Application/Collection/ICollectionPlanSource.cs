namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public interface ICollectionPlanSource
{
    CollectionPlan Current { get; }
    event Action? PlanChanged;
}
