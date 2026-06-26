namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Scoped registration for one reqContractDetails round-trip: holds the awaiter the requester drives and, on
// Dispose, evicts the reqId from the wrapper's correlator. reqId is monotonic (never reused), so scoping the
// release to a `using` is what keeps the correlator from growing unbounded over the connection's lifetime.
internal readonly struct ContractDetailsRequest(
    IbWrapper wrapper, int reqId, Task<IReadOnlyList<IbContractDetailsResult>> completion) : IDisposable
{
    public Task<IReadOnlyList<IbContractDetailsResult>> Completion { get; } = completion;

    public void Dispose() => wrapper.ReleaseContractDetails(reqId);
}
