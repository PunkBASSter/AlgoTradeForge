namespace AlgoTradeForge.Application.Events;

public interface ITradeDbWriter
{
    /// <summary>
    /// Parses events.jsonl and inserts run/order/trade data into trades.sqlite.
    /// </summary>
    void WriteFromJsonl(string runFolderPath, RunIdentity identity, RunSummary summary);

    /// <summary>
    /// Deletes existing data for this run folder, then re-inserts from JSONL.
    /// </summary>
    void RebuildFromJsonl(string runFolderPath, RunIdentity identity, RunSummary summary);
}
