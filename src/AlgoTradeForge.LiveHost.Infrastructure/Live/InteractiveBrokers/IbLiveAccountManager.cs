using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Storage.Threading;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// IB inversion of BinanceLiveAccountManager: Binance keeps a per-account connector dictionary (account ==
// transport); IB has N sub-accounts over ONE socket, so this manager owns a SINGLE shared IbLiveConnector and
// hands it back for ANY account name. GetOrCreateAsync collapses the account key to that one connector (routing
// to the right AccountTarget happens INSIDE the connector via config.AccountName). Connect is gated so concurrent
// session starts establish the socket exactly once.
internal sealed class IbLiveAccountManager(Func<IbLiveConnector> connectorFactory) : ILiveAccountManager, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IbLiveConnector? _connector;

    public async Task<ILiveConnector> GetOrCreateAsync(string accountName, CancellationToken ct = default)
    {
        // Fast path: the shared connector is already running — any account resolves to it.
        if (_connector is { Status: LiveSessionStatus.Running } running)
            return running;

        using var _ = await _gate.LockAsync(ct);

        if (_connector is { Status: LiveSessionStatus.Running })
            return _connector;

        // A non-running connector (errored/stopped) is replaced.
        if (_connector is not null)
        {
            await _connector.DisposeAsync();
            _connector = null;
        }

        var connector = connectorFactory();
        await connector.ConnectAsync(ct);
        _connector = connector;
        return connector;
    }

    public ILiveConnector? Get(string accountName) =>
        _connector is { Status: LiveSessionStatus.Running } ? _connector : null;

    public IReadOnlyList<string> GetActiveAccountNames() =>
        _connector is { Status: LiveSessionStatus.Running } ? [_connector.AccountName] : [];

    public async Task<bool> TryRemoveAsync(string accountName, CancellationToken ct = default)
    {
        using var _ = await _gate.LockAsync(ct);
        if (_connector is null)
            return false;

        await _connector.DisposeAsync();
        _connector = null;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connector is not null)
            await _connector.DisposeAsync();
        _connector = null;
        _gate.Dispose();
    }
}
