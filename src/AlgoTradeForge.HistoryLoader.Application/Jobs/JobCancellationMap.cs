using System.Collections.Concurrent;

namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public sealed class JobCancellationMap : IJobCancellationMap
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _map = new();

    public CancellationToken Register(string jobId, CancellationToken linkedTo)
    {
        var cts = _map.GetOrAdd(jobId, _ => CancellationTokenSource.CreateLinkedTokenSource(linkedTo));
        return cts.Token;
    }

    public void Trip(string jobId)
    {
        if (_map.TryGetValue(jobId, out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Remove(string jobId)
    {
        if (_map.TryRemove(jobId, out var cts))
            cts.Dispose();
    }
}
