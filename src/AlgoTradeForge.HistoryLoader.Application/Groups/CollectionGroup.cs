namespace AlgoTradeForge.HistoryLoader.Application.Groups;

public sealed record CollectionGroup(
    string Name,
    bool Enabled,
    IReadOnlyList<string> Exchanges,
    GroupAssets Assets,
    IReadOnlyDictionary<string, GroupFeed> Feeds,
    IReadOnlyDictionary<string, GroupDerived>? Derived,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? SymbolOverrides);

public sealed record GroupAssets(IReadOnlyList<string> Symbols, string HistoryStart);
public sealed record GroupFeed(string Collect, IReadOnlyList<string>? Intervals, string? Format);
public sealed record GroupDerived(string Source, string? Type, string? Threshold, string? SourceInterval, string Materialize);
