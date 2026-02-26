using System.Collections;

namespace AlgoTradeForge.Domain.Indicators;

public sealed class IndicatorBuffer<T>(string name, bool skipDefaultValues = false) : IReadOnlyList<T>
{
    private readonly List<T> _data = [];

    public string Name { get; } = name;
    public bool SkipDefaultValues { get; } = skipDefaultValues;
    public Action<string, int, T>? OnRevised { get; set; }

    public int Count => _data.Count;
    public T this[int index] => _data[index];

    public void Append(T value) => _data.Add(value);

    public void Set(int index, T value) => _data[index] = value;

    public void Revise(int index, T value)
    {
        _data[index] = value;
        OnRevised?.Invoke(Name, index, value);
    }

    public IEnumerator<T> GetEnumerator() => _data.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
