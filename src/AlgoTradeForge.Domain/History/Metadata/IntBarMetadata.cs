namespace AlgoTradeForge.Domain.History.Metadata;

public sealed record IntBarMetadata : SampleMetadata<IntBar>
{
    public Asset? Asset { get; init; }
}
