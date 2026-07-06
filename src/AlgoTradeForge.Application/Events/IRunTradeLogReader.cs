namespace AlgoTradeForge.Application.Events;

/// <summary>
/// One round-trip trade reconstructed from a run folder's <c>events.jsonl</c>.
/// Prices/commissions are tick-denominated longs (Domain money convention);
/// callers convert via ScaleContext.
/// </summary>
public sealed record RunTradeRecord
{
    public required DateTimeOffset EntryTime { get; init; }
    public required long EntryPrice { get; init; }
    public DateTimeOffset? ExitTime { get; init; }
    public long? ExitPrice { get; init; }
    public required string Side { get; init; }
    public required decimal Quantity { get; init; }
    public long? Pnl { get; init; }
    public long Commission { get; init; }
    public long? TakeProfitPrice { get; init; }
    public long? StopLossPrice { get; init; }
}

/// <summary>
/// Reconstructs round-trip trades from a run folder's <c>events.jsonl</c> by pairing
/// <c>ord.fill</c> events per asset (position leaves zero → entry, returns to zero → exit)
/// and enriching SL/TP from <c>grp</c> transitions. Universal across strategies: works for
/// registry-managed groups AND raw order flows (e.g. a manual flatten-at-close fill).
/// </summary>
public interface IRunTradeLogReader
{
    Task<IReadOnlyList<RunTradeRecord>> Read(string runFolderPath, CancellationToken ct = default);
}
