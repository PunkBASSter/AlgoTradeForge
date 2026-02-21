namespace AlgoTradeForge.Application.Events;

public interface IEventIndexBuilder
{
    /// <summary>
    /// Builds index.sqlite from events.jsonl in the run folder. No-op if index already exists.
    /// </summary>
    void Build(string runFolderPath);

    /// <summary>
    /// Deletes existing index and rebuilds from events.jsonl. Idempotent.
    /// </summary>
    void Rebuild(string runFolderPath);
}
