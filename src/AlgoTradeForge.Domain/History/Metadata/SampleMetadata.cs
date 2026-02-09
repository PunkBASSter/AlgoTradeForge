using System.Text;

namespace AlgoTradeForge.Domain.History.Metadata;

/// <summary>
/// Metadata of a data sample in TimeSeries or other historical data structures.
/// </summary>
/// <typeparam name="T"></typeparam>
public record SampleMetadata<T>
{
    public virtual int ToIntMultiplier { get; } = 100;
    public virtual string? Label { get; }
}
