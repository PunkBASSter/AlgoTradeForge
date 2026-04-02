using System.Collections;
using AlgoTradeForge.Domain.Collections;

namespace AlgoTradeForge.Domain.Indicators;

public sealed class IndicatorBuffer<T>(string name, bool skipDefaultValues = false, int? exportChartId = null) : IReadOnlyList<T>
{
    private readonly List<T> _data = [];
    private RingBuffer<T>? _ring;

    public string Name { get; } = name;
    public bool SkipDefaultValues { get; } = skipDefaultValues;
    public int? ExportChartId { get; set; } = exportChartId;
    public Action<string, int, T>? OnRevised { get; set; }

    /// <summary>
    /// Total number of values ever appended (not the number currently retained).
    /// When backed by a ring buffer, only the last <c>capacity</c> values are
    /// accessible by index; accessing evicted indices throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public int Count => _ring?.Count ?? _data.Count;
    public T this[int index] => _ring is not null ? _ring[index] : _data[index];

    /// <summary>
    /// Switches to a bounded ring buffer. Must be called before data is added.
    /// </summary>
    public void SetCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (_ring is not null)
            throw new InvalidOperationException("Capacity already set.");
        if (_data.Count > 0)
            throw new InvalidOperationException("Cannot set capacity after data has been added.");
        _ring = new RingBuffer<T>(capacity);
    }

    public void Append(T value)
    {
        if (_ring is not null)
            _ring.Add(value);
        else
            _data.Add(value);
    }

    public void Set(int index, T value)
    {
        if (_ring is not null)
            _ring.Set(index, value); // silent no-op if evicted
        else
            _data[index] = value;
    }

    /// <summary>
    /// Revises a previously written value. Throws if the index has been evicted from a
    /// bounded buffer — this indicates the indicator's capacity is too small for its
    /// revision window, which is a logic bug.
    /// </summary>
    public void Revise(int index, T value)
    {
        if (_ring is not null)
        {
            if (!_ring.IsRetained(index))
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Cannot revise evicted index {index} in buffer '{Name}'. Increase CapacityLimit.");
            _ring.Set(index, value);
        }
        else
        {
            _data[index] = value;
        }
        OnRevised?.Invoke(Name, index, value);
    }

    public IEnumerator<T> GetEnumerator() =>
        _ring is not null ? _ring.GetEnumerator() : _data.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
