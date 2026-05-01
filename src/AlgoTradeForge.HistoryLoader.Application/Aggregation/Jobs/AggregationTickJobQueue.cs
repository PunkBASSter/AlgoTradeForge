using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// Bounded <see cref="Channel{T}"/> implementation of <see cref="IAggregationTickJobQueue"/>.
/// Shape mirrors <see cref="AggregationJobQueue"/> intentionally — same capacity knob
/// (<c>maxQueueDepth</c>), same DropWrite-disabled semantics — so the only behavioral
/// difference is which worker pool drains it.
/// </summary>
public sealed class AggregationTickJobQueue : IAggregationTickJobQueue
{
    private readonly Channel<AggregationJob> _channel;

    public AggregationTickJobQueue(IOptions<HistoryLoaderOptions> options)
    {
        var capacity = options.Value.Aggregator.MaxQueueDepth;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _channel = Channel.CreateBounded<AggregationJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            // MaxConcurrentTickJobs=1 (TRD §6.5) → exactly one worker drains this channel.
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool TryWrite(AggregationJob job) => _channel.Writer.TryWrite(job);

    public ChannelReader<AggregationJob> Reader => _channel.Reader;

    public int CurrentDepth => _channel.Reader.CanCount ? _channel.Reader.Count : 0;
}
