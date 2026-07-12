using System.Threading.Channels;

namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public sealed class JobWakeupQueue : IJobWakeupQueue
{
    private readonly Channel<string> _channel;

    public JobWakeupQueue(int capacity) =>
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public bool TryEnqueue(string jobId) => _channel.Writer.TryWrite(jobId);

    public IAsyncEnumerable<string> Reader(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    public int SeedFromQueued(IEnumerable<string> jobIds)
    {
        var count = 0;
        foreach (var id in jobIds)
            if (_channel.Writer.TryWrite(id))
                count++;
        return count;
    }
}
