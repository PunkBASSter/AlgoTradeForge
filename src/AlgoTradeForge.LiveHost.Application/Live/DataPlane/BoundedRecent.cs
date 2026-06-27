using AlgoTradeForge.Domain.Collections;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

// Thread-safe "most recent N" view backed by a RingBuffer. Replaces the duplicated
// Queue<T> + Lock + manual-eviction pattern across the venue/tick bar sources: Add()
// auto-evicts the oldest once full; Snapshot() returns an independent oldest→newest copy.
public sealed class BoundedRecent<T>
{
    private readonly RingBuffer<T> _ring;
    private readonly Lock _gate = new();

    public BoundedRecent(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _ring = new RingBuffer<T>(capacity);
    }

    public void Add(T item)
    {
        lock (_gate) _ring.Add(item);
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate) return _ring.ToArray();
    }
}
