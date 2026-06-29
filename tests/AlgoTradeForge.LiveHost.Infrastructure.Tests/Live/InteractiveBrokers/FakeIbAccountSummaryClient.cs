using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

// Fake IIbAccountSummaryClient for unit tests: immediately resolves the awaiter with pre-set rows
// when RequestAccountSummary is called (no socket, no pump thread).
internal sealed class FakeIbAccountSummaryClient(IEnumerable<IbAccountSummaryRow> rows) : IIbAccountSummaryClient
{
    private readonly IbAccountSummaryRow[] _rows = [.. rows];
    private int _nextReqId;
    private TaskCompletionSource<IReadOnlyList<IbAccountSummaryRow>>? _pending;

    public int NextReqId() => Interlocked.Increment(ref _nextReqId);

    public Task<IReadOnlyList<IbAccountSummaryRow>> RegisterAccountSummary(int reqId)
    {
        _pending = new TaskCompletionSource<IReadOnlyList<IbAccountSummaryRow>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _pending.Task;
    }

    public void RequestAccountSummary(int reqId, string group, string tags) =>
        _pending?.TrySetResult(_rows);
}
