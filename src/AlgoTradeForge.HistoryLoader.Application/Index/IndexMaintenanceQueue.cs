using System.Threading.Channels;

namespace AlgoTradeForge.HistoryLoader.Application.Index;

public sealed class IndexMaintenanceQueue : IIndexMaintenance
{
    private readonly Channel<IndexWork> _channel = Channel.CreateUnbounded<IndexWork>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<IndexWork> Reader => _channel.Reader;

    public void Enqueue(IndexWork work) => _channel.Writer.TryWrite(work);
}
