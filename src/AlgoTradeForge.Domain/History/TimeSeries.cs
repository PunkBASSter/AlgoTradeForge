using AlgoTradeForge.Domain.History.Metadata;
using System.Collections;

namespace AlgoTradeForge.Domain.History;

public class TimeSeries<T> : IReadOnlyList<T>
{
    private T[] _buffer;
    private int _head;
    private int _count;
    private readonly int? _capacity;
    private readonly TimeSpan _timeSpan;
    private DateTimeOffset _startTime;
    private SampleMetadata<T>? _metadata; //Maybe will be used for transformations, indicators, etc. TODO add to ctor?

    public int Count => _count;

    public TimeSeries(DateTimeOffset startTime, TimeSpan step, int? capacity = null, IEnumerable<T>? initialData = null)
    {
        if (capacity.HasValue)
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity.Value, 1);

        _timeSpan = step;
        _startTime = startTime;
        _capacity = capacity;
        _buffer = new T[capacity ?? 8];

        if (initialData != null)
        {
            foreach (var item in initialData)
                Add(item);
        }
    }

    public void Add(T item)
    {
        if (_capacity.HasValue)
        {
            var cap = _capacity.Value;
            if (_count < cap)
            {
                _buffer[(_head + _count) % cap] = item;
                _count++;
            }
            else
            {
                _buffer[_head] = item;
                _head = (_head + 1) % cap;
                _startTime += _timeSpan;
            }
        }
        else
        {
            if (_count == _buffer.Length)
                Array.Resize(ref _buffer, _buffer.Length * 2);

            _buffer[_count] = item;
            _count++;
        }
    }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return _buffer[(_head + index) % _buffer.Length];
        }
    }

    public T this[DateTime timestamp]
    {
        get
        {
            var offset = timestamp - _startTime;
            var index = (int)(offset.Ticks / _timeSpan.Ticks);
            return this[index];
        }
    }

    public T this[DateTimeOffset timestamp]
    {
        get
        {
            var offset = timestamp - _startTime;
            var index = (int)(offset.Ticks / _timeSpan.Ticks);
            return this[index];
        }
    }

    public DateTimeOffset GetTimestamp(int index) => _startTime + TimeSpan.FromTicks(_timeSpan.Ticks * index);

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}