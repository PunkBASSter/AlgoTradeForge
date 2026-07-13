using AlgoTradeForge.HistoryLoader.Application.Jobs;

namespace AlgoTradeForge.HistoryLoader.Tests.TestHelpers;

internal sealed class RecordingSink : IJobProgressSink
{
    public bool WasStarted { get; private set; }
    public string? StartedPayload { get; private set; }
    public List<string> Reports { get; } = [];
    public bool WasCompleted { get; private set; }
    public string? CompletedPayload { get; private set; }
    public string? FailCode { get; private set; }
    public string? FailMessage { get; private set; }
    public string? CancelReason { get; private set; }

    public Task Started(string startedPayloadJson, CancellationToken ct = default)
    {
        WasStarted = true;
        StartedPayload = startedPayloadJson;
        return Task.CompletedTask;
    }

    public Task Report(string progressJson, CancellationToken ct = default)
    {
        Reports.Add(progressJson);
        return Task.CompletedTask;
    }

    public Task Complete(string resultPayloadJson, CancellationToken ct = default)
    {
        WasCompleted = true;
        CompletedPayload = resultPayloadJson;
        return Task.CompletedTask;
    }

    public Task Fail(string code, string message, CancellationToken ct = default)
    {
        FailCode = code;
        FailMessage = message;
        return Task.CompletedTask;
    }

    public Task Cancel(string reason, CancellationToken ct = default)
    {
        CancelReason = reason;
        return Task.CompletedTask;
    }
}
