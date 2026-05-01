using System.Threading.Channels;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// Bounded FIFO of pending jobs (TRD §6.5). Backed by <see cref="Channel{T}"/> with capacity
/// <c>aggregator.maxQueueDepth</c>. The endpoint enqueues via the registry (atomic w.r.t.
/// duplicate-feed_id detection); the worker host consumes through <see cref="Reader"/>.
/// </summary>
public interface IAggregationJobQueue
{
    /// <summary>
    /// Non-blocking enqueue. Returns <c>false</c> when the queue is at capacity — the
    /// endpoint surfaces this as 503 with <c>Retry-After</c>.
    /// </summary>
    bool TryWrite(AggregationJob job);

    /// <summary>Reader for the worker host. Single channel, multiple readers.</summary>
    ChannelReader<AggregationJob> Reader { get; }

    /// <summary>Approximate queue depth — observable but not transactional.</summary>
    int CurrentDepth { get; }
}
