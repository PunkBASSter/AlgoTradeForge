using System.Collections;

namespace AlgoTradeForge.Domain.History;

public class TimeSeries<T> : IReadOnlyList<T>
{
    private T[] _buffer;
    private int _count;

    public int Count => _count;

    public TimeSeries(int initialCapacity = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        _buffer = new T[initialCapacity];
    }

    public TimeSeries(IEnumerable<T> initialData) : this()
    {
        foreach (var item in initialData)
            Add(item);
    }

    public void Add(T item)
    {
        if (_count == _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);

        _buffer[_count] = item;
        _count++;
    }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return _buffer[index];
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
            yield return _buffer[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
