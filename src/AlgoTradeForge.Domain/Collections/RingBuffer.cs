using System.Collections;

namespace AlgoTradeForge.Domain.Collections;

// Append-only ring buffer that retains the last `capacity` items but reports
// Count as total items ever added. Designed for incremental indicator computation
// where only the most recent items are accessed via indexer.
// Accessing evicted indices throws ArgumentOutOfRangeException.
public sealed class RingBuffer<T>(int capacity) : IReadOnlyList<T>
{
    private readonly T[] _items = new T[capacity];
    private int _head;

    public int Count { get; private set; }

    public T this[int index]
    {
        get
        {
            var age = Count - 1 - index;
            if ((uint)age >= (uint)_items.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _items[((_head - 1 - age) % _items.Length + _items.Length) % _items.Length];
        }
    }

    public void Add(T item)
    {
        _items[_head] = item;
        _head = (_head + 1) % _items.Length;
        Count++;
    }

    public IEnumerator<T> GetEnumerator()
    {
        var stored = Math.Min(Count, _items.Length);
        var start = (_head - stored + _items.Length) % _items.Length;
        for (var i = 0; i < stored; i++)
            yield return _items[(start + i) % _items.Length];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
