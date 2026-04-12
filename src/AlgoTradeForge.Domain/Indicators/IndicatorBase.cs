namespace AlgoTradeForge.Domain.Indicators;

public abstract class IndicatorBase<TBuff>
{
    private const int DefaultMinCapacity = 256;

    public virtual string Name => GetType().Name;
    public virtual int MinimumHistory { get; } = 1;
    public virtual int? CapacityLimit { get; } = null;
    public abstract IReadOnlyDictionary<string, IndicatorBuffer<TBuff>> Buffers { get; }

    /// <summary>
    /// Applies buffer capacity to all buffers. Call at the end of the constructor,
    /// after <see cref="Buffers"/> is populated.
    /// <para><c>CapacityLimit = null</c> → auto (<c>Max(MinimumHistory * 2, 256)</c>).
    /// <c>CapacityLimit = 0</c> → unbounded. <c>CapacityLimit = N</c> → use N.</para>
    /// </summary>
    protected void ApplyBufferCapacity()
    {
        var limit = CapacityLimit;
        if (limit == 0) return; // explicitly unbounded
        var effective = limit ?? Math.Max(MinimumHistory * 2, DefaultMinCapacity);
        foreach (var (_, buffer) in Buffers)
            buffer.SetCapacity(effective);
    }
}
