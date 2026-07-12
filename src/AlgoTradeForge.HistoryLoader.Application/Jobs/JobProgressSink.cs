using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Index;

namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public sealed class JobProgressSink(string jobId, IHistoryIndex index, IJobEventSignal signal) : IJobProgressSink
{
    private string _state = "queued";

    public async Task Report(string progressJson, CancellationToken ct = default)
    {
        await index.UpdateJob(jobId, _state, progressJson: progressJson, ct: ct);
        await index.AppendJobEvent(jobId, "progress", progressJson, ct);
        signal.Signal(jobId);
    }

    public async Task Started(string startedPayloadJson, CancellationToken ct = default)
    {
        _state = "running";
        await index.UpdateJob(jobId, "running", ct: ct);
        await index.AppendJobEvent(jobId, "started", startedPayloadJson, ct);
        signal.Signal(jobId);
    }

    public async Task Complete(string resultPayloadJson, CancellationToken ct = default)
    {
        await index.UpdateJob(jobId, "complete", ct: ct);
        await index.AppendJobEvent(jobId, "complete", resultPayloadJson, ct);
        signal.Signal(jobId);
        signal.Evict(jobId);
    }

    public async Task Fail(string code, string message, CancellationToken ct = default)
    {
        var errorJson = JsonSerializer.Serialize(new { code, message });
        await index.UpdateJob(jobId, "error", error: errorJson, ct: ct);
        await index.AppendJobEvent(jobId, "error", errorJson, ct);
        signal.Signal(jobId);
        signal.Evict(jobId);
    }

    public async Task Cancel(string reason, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { reason });
        await index.UpdateJob(jobId, "cancelled", ct: ct);
        await index.AppendJobEvent(jobId, "cancelled", payload, ct);
        signal.Signal(jobId);
        signal.Evict(jobId);
    }
}
