namespace AlgoTradeForge.Application.Events;

public interface IRunSink : ISink, IDisposable
{
    string RunFolderPath { get; }
    void WriteMeta(RunSummary summary);
}

public interface IRunSinkFactory
{
    IRunSink Create(RunIdentity identity);
}
