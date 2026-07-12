namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public interface IJobProgressSink
{
    Task Report(string progressJson, CancellationToken ct = default);
    Task Started(string startedPayloadJson, CancellationToken ct = default);
    Task Complete(string resultPayloadJson, CancellationToken ct = default);
    Task Fail(string code, string message, CancellationToken ct = default);
    Task Cancel(string reason, CancellationToken ct = default);
}
