namespace AlgoTradeForge.Domain.History.Metadata;

public sealed record IntBarMetadata : SampleMetadata<Int64Bar>
{
    public Asset? Asset { get; init; }
}
