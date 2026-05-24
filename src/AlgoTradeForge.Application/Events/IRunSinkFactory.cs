namespace AlgoTradeForge.Application.Events;

public interface IRunSink : ISink, IAsyncDisposable
{
    string RunFolderPath { get; }
    Task WriteMeta(RunSummary summary, CancellationToken ct = default);
    Task Flush(CancellationToken ct = default);
}

public interface IRunSinkFactory
{
    IRunSink Create(RunIdentity identity);
}
