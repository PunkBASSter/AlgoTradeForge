using System.Threading.Channels;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// Bounded FIFO of pending jobs. The endpoint enqueues via the registry (atomic w.r.t.
/// duplicate-feed_id detection); the worker host consumes through <see cref="Reader"/>.
/// </summary>
public interface IAggregationJobQueue
{
    /// <summary>Non-blocking enqueue. Returns <c>false</c> at capacity (endpoint maps to 503).</summary>
    bool TryWrite(AggregationJob job);

    ChannelReader<AggregationJob> Reader { get; }

    /// <summary>Approximate queue depth — observable but not transactional.</summary>
    int CurrentDepth { get; }
}
