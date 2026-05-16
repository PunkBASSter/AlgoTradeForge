namespace AlgoTradeForge.Application.Events;

public interface IRunSink : ISink, IDisposable
{
    string RunFolderPath { get; }
    Task WriteMeta(RunSummary summary, CancellationToken ct = default);
}

public interface IRunSinkFactory
{
    IRunSink Create(RunIdentity identity);
}
