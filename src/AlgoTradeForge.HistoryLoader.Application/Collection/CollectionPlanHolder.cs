namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class CollectionPlanHolder : ICollectionPlanSource
{
    private volatile CollectionPlan _current = CollectionPlan.Empty;

    public CollectionPlan Current => _current;

    public event Action? PlanChanged;

    public void Publish(CollectionPlan plan)
    {
        _current = plan;
        PlanChanged?.Invoke();
    }
}
