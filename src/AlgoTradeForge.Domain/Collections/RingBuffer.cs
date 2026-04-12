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

    /// <summary>Returns true if the absolute index is within the retained window.</summary>
    public bool IsRetained(int index)
    {
        var age = Count - 1 - index;
        return (uint)age < (uint)_items.Length;
    }

    public T this[int index]
    {
        get
        {
            var age = Count - 1 - index;
            if ((uint)age >= (uint)_items.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _items[SlotFor(age)];
        }
    }

    public void Add(T item)
    {
        _items[_head] = item;
        _head = (_head + 1) % _items.Length;
        Count++;
    }

    /// <summary>
    /// Writes a value at an absolute index. Silent no-op if the index has been evicted or is out of range.
    /// </summary>
    public void Set(int index, T value)
    {
        var age = Count - 1 - index;
        if ((uint)age >= (uint)_items.Length)
            return;
        _items[SlotFor(age)] = value;
    }

    private int SlotFor(int age) =>
        ((_head - 1 - age) % _items.Length + _items.Length) % _items.Length;

    public IEnumerator<T> GetEnumerator()
    {
        var stored = Math.Min(Count, _items.Length);
        var start = (_head - stored + _items.Length) % _items.Length;
        for (var i = 0; i < stored; i++)
            yield return _items[(start + i) % _items.Length];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
