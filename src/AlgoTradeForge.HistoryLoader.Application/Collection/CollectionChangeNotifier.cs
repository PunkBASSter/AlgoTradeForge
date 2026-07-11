namespace AlgoTradeForge.HistoryLoader.Application.Collection;

/// <summary>Raised after a collector persists a discovered first month — DesiredStateService
/// re-runs the pipeline so CollectionPlan.EffectiveStart catches up without a group edit
/// (spec §3.2).</summary>
public sealed class CollectionChangeNotifier
{
    public event Action? DiscoveryRecorded;
    public void NotifyDiscoveryRecorded() => DiscoveryRecorded?.Invoke();
}
