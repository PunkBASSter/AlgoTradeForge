namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IAccountTargetFactory
{
    Task<IAccountTarget> Create(string account, CancellationToken ct = default);
}
