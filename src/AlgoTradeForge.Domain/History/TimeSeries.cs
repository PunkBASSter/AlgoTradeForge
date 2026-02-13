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

    public TimeSpan Step => _timeSpan;

    public DateTimeOffset StartTime => _startTime;

    public DateTimeOffset GetTimestamp(int index) => _startTime + TimeSpan.FromTicks(_timeSpan.Ticks * index);

    public TimeSeries<T> Slice(DateTimeOffset from, DateTimeOffset to)
    {
        if (from >= to)
            return new TimeSeries<T>(from, _timeSpan);

        if (_count == 0)
            return new TimeSeries<T>(from, _timeSpan);

        var startOffset = from - _startTime;
        var startIndex = (int)Math.Max(0, (long)Math.Ceiling((double)startOffset.Ticks / _timeSpan.Ticks));

        var endOffset = to - _startTime;
        var endIndex = (int)Math.Min(_count, (long)Math.Ceiling((double)endOffset.Ticks / _timeSpan.Ticks));

        if (startIndex >= endIndex)
            return new TimeSeries<T>(from, _timeSpan);

        var sliceStart = _startTime + TimeSpan.FromTicks(_timeSpan.Ticks * startIndex);
        var result = new TimeSeries<T>(sliceStart, _timeSpan);

        for (var i = startIndex; i < endIndex; i++)
            result.Add(this[i]);

        return result;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}