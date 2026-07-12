using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class JobSseWriter
{
    // Tail loop over the durable job_events store, extracted for HTTP-free unit testing.
    // Capture-before-drain: snapshot the next-event signal BEFORE reading the tail. JobProgressSink
    // appends the durable event, THEN Signals — so any event added between capture and drain has
    // already made the row visible and fired the captured signal, and the next drain picks it up.
    // Capturing after the drain would TOCTOU and lose a terminal event appended in that window.
    internal static async Task TailForTest(
        string jobId,
        int lastEventId,
        IHistoryIndex index,
        IJobEventSignal signal,
        Func<int, string, string, Task> emit,
        CancellationToken ct)
    {
        var lastSent = lastEventId;
        while (!ct.IsCancellationRequested)
        {
            var next = signal.Next(jobId);

            var fresh = await index.GetJobEventsAfter(jobId, lastSent, ct);
            if (lastSent == lastEventId
                && lastEventId > 0
                && fresh.Count == 0
                && await index.GetLastEventSeq(jobId, ct) > 0)
            {
                // Resume past a trimmed last-known event — replay from the start of the retained tail.
                fresh = await index.GetJobEventsAfter(jobId, 0, ct);
            }

            foreach (var ev in fresh)
            {
                await emit(ev.Seq, ev.Kind, ev.PayloadJson);
                lastSent = ev.Seq;
                if (ev.Kind is "complete" or "error" or "cancelled") return;
            }

            await next.WaitAsync(ct);
        }
    }
}
