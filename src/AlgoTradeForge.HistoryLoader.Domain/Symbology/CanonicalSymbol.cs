namespace AlgoTradeForge.HistoryLoader.Domain.Symbology;

public sealed record CanonicalSymbol(string Base, string Quote, InstrumentKind Kind, string? Expiry)
{
    public override string ToString() => Kind switch
    {
        InstrumentKind.Spot        => $"{Base}/{Quote}",
        InstrumentKind.Perpetual   => $"{Base}/{Quote}-PERP",
        InstrumentKind.DatedFuture => $"{Base}/{Quote}-FUT-{Expiry}",
        _ => throw new InvalidOperationException(),
    };
}
