namespace AlgoTradeForge.Domain.Live;

public interface ILiveAccountManager
{
    Task<ILiveConnector> GetOrCreateAsync(string accountName, CancellationToken ct = default);
    ILiveConnector? Get(string accountName);
    IReadOnlyList<string> GetActiveAccountNames();
}
