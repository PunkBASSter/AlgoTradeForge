using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Optimization.Fitness;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
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

    public BoundedTrialQueue(int capacity, IFitnessFunction fitnessFunction)
    {
        _capacity = capacity;
        _priorityExtractor = fitnessFunction.Evaluate;
        // higher fitness = better, _ascending defaults to false
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

    /// <summary>
    /// Drains the heap, removes duplicate trials (same parameters + asset), and returns sorted best-first.
    /// Keeps the first (best) occurrence of each duplicate. Not thread-safe.
    /// </summary>
    public List<BacktestRunRecord> DeduplicateAndDrainSorted()
    {
        var items = DrainSorted();
        var seen = new HashSet<string>();
        return items.Where(r => seen.Add(BuildTrialKey(r))).ToList();
    }

    internal static string BuildTrialKey(BacktestRunRecord record)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(record.DataSubscription.AssetName)
          .Append(':').Append(record.DataSubscription.Exchange)
          .Append(':').Append(record.DataSubscription.TimeFrame)
          .Append('|');

        var first = true;
        foreach (var key in record.Parameters.Keys
            .Where(k => !IsDataSubscriptionParam(record.Parameters[k]))
            .OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!first) sb.Append('|');
            first = false;
            sb.Append(key).Append('=');
            AppendValue(sb, record.Parameters[key]);
        }
        return sb.ToString();
    }

    private static bool IsDataSubscriptionParam(object value) =>
        value is DataSubscription or DataSubscriptionDto;

    private static void AppendValue(System.Text.StringBuilder sb, object value) =>
        ParameterKeyBuilder.AppendValue(sb, value);
}
