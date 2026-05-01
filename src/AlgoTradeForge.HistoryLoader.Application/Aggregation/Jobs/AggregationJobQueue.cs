using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// Bounded <see cref="Channel{T}"/> implementation of <see cref="IAggregationJobQueue"/>.
/// Capacity comes from <c>aggregator.maxQueueDepth</c>; <see cref="BoundedChannelFullMode.DropWrite"/>
/// is intentionally NOT used — full → <c>TryWrite</c> returns false so the endpoint can return
/// 503 immediately rather than silently lose the job.
/// </summary>
public sealed class AggregationJobQueue : IAggregationJobQueue
{
    private readonly Channel<AggregationJob> _channel;

    public AggregationJobQueue(IOptions<HistoryLoaderOptions> options)
    {
        var capacity = options.Value.Aggregator.MaxQueueDepth;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _channel = Channel.CreateBounded<AggregationJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public bool TryWrite(AggregationJob job) => _channel.Writer.TryWrite(job);

    public ChannelReader<AggregationJob> Reader => _channel.Reader;

    /// <summary>
    /// <see cref="ChannelReader{T}.Count"/> is the canonical depth on bounded channels and
    /// updates atomically on read/write — no manual counter needed.
    /// </summary>
    public int CurrentDepth => _channel.Reader.CanCount ? _channel.Reader.Count : 0;
}
