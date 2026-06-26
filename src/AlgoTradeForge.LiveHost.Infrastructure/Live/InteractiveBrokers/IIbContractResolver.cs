namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal interface IIbContractResolver
{
    Task<ResolvedIbContract> Resolve(IbContract spec, CancellationToken ct = default);
}
