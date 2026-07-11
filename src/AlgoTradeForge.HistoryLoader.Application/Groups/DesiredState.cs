namespace AlgoTradeForge.HistoryLoader.Application.Groups;

public sealed record GroupConflict(string Key, string Kind, IReadOnlyList<string> Groups, string Message); // Kind: format | derived-definition
public sealed record UnsupportedTuple(string Exchange, string Canonical, string Reason);

public sealed record DesiredState(
    IReadOnlyList<DesiredTuple> Tuples,
    IReadOnlyList<UnsupportedTuple> Unsupported,
    IReadOnlyList<GroupConflict> Conflicts);
