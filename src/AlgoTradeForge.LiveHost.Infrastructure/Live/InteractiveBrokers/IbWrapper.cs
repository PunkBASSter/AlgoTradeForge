using System.Collections.Concurrent;
using IBApi;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Derives from DefaultEWrapper so only the callbacks Plan 1 exercises are overridden; every other EWrapper
// member (incl. 10.45 ProtoBuf variants) inherits an empty body. Accumulates contractDetails per reqId and
// completes the awaiter on contractDetailsEnd (a single reqContractDetails returns many months for a futures
// family). Callbacks fire on the single EReader pump thread, so per-reqId accumulation is not concurrent.
// Plan 3/4 grow this with tick / order / fill callbacks.
internal sealed class IbWrapper : DefaultEWrapper
{
    private sealed class Pending
    {
        public List<IbContractDetailsResult> Items { get; } = [];
        public TaskCompletionSource<IReadOnlyList<IbContractDetailsResult>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly TaskCompletionSource<int> _nextValidId =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, Pending> _byReq = new();

    public Task<int> NextValidId => _nextValidId.Task;

    public Task<IReadOnlyList<IbContractDetailsResult>> AwaitContractDetails(int reqId) =>
        _byReq.GetOrAdd(reqId, _ => new Pending()).Completion.Task;

    public override void nextValidId(int orderId) => _nextValidId.TrySetResult(orderId);

    public override void contractDetails(int reqId, ContractDetails contractDetails)
    {
        if (_byReq.TryGetValue(reqId, out var pending))
            pending.Items.Add(new IbContractDetailsResult(
                contractDetails.Contract.ConId,
                contractDetails.Contract.LocalSymbol,
                contractDetails.Contract.LastTradeDateOrContractMonth ?? ""));
    }

    public override void contractDetailsEnd(int reqId)
    {
        if (_byReq.TryGetValue(reqId, out var pending))
            pending.Completion.TrySetResult(pending.Items.ToArray());
    }

    public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        // Connectivity / data-farm notices arrive with id == -1; never correlate those to a request.
        if (id >= 0 && _byReq.TryGetValue(id, out var pending))
            pending.Completion.TrySetException(new IbRequestException(errorCode, errorMsg));
    }
}
