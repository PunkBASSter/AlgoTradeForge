namespace AlgoTradeForge.Application.Events;

public interface IPostRunPipeline
{
    PostRunResult Execute(string runFolderPath, RunIdentity identity, RunSummary summary);
}

public sealed record PostRunResult(
    bool IndexBuilt,
    bool TradesInserted,
    string? IndexError,
    string? TradesError);
