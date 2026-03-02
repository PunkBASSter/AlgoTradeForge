using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Reporting;
using static AlgoTradeForge.Domain.Reporting.MetricNames;

namespace AlgoTradeForge.Application.Optimization;

/// <summary>
/// Thread-safe bounded priority queue that keeps the top-N trials by sort metric.
/// Uses a min-heap so the worst item is always at the top for O(log N) eviction.
/// </summary>
public sealed class BoundedTrialQueue
{
    private readonly PriorityQueue<BacktestRunRecord, double> _heap = new();
    private readonly int _capacity;
    private readonly Func<PerformanceMetrics, double> _priorityExtractor;
    private readonly bool _ascending; // true for MaxDrawdownPct (lower is better)
    private readonly object _lock = new();

    public BoundedTrialQueue(int capacity, string sortBy)
    {
        _capacity = capacity;
        (_priorityExtractor, _ascending) = sortBy switch
        {
            SharpeRatio      => ((Func<PerformanceMetrics, double>)(m => m.SharpeRatio), false),
            NetProfit        => (m => (double)m.NetProfit, false),
            SortinoRatio     => (m => m.SortinoRatio, false),
            ProfitFactor     => (m => m.ProfitFactor, false),
            WinRatePct       => (m => m.WinRatePct, false),
            MaxDrawdownPct   => (m => m.MaxDrawdownPct, true),
            _                => (m => m.SharpeRatio, false),
        };
    }

    /// <summary>
    /// Attempts to add a trial to the queue. If full, evicts the worst item
    /// only when the new trial is better. Thread-safe.
    /// </summary>
    public void TryAdd(BacktestRunRecord record)
    {
        var rawPriority = _priorityExtractor(record.Metrics);
        // For ascending sorts (e.g. MaxDrawdownPct where lower is better),
        // negate so the min-heap evicts the highest drawdown first.
        var heapPriority = _ascending ? -rawPriority : rawPriority;

        lock (_lock)
        {
            if (_heap.Count < _capacity)
            {
                _heap.Enqueue(record, heapPriority);
            }
            else if (_heap.TryPeek(out _, out var worstPriority) && heapPriority > worstPriority)
            {
                _heap.DequeueEnqueue(record, heapPriority);
            }
        }
    }

    /// <summary>
    /// Drains the heap and returns trials sorted best-first.
    /// Not thread-safe — call only after all producers are done.
    /// </summary>
    public List<BacktestRunRecord> DrainSorted()
    {
        var items = new List<BacktestRunRecord>(_heap.Count);
        while (_heap.TryDequeue(out var record, out _))
            items.Add(record);

        // Heap drains worst-first, reverse for best-first
        items.Reverse();
        return items;
    }
}
