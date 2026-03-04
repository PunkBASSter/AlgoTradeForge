using System.Collections.Concurrent;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Live;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.Live.Binance;

public sealed class BinanceLiveAccountManager(
    IOptions<BinanceLiveOptions> options,
    IOrderValidator orderValidator,
    ILoggerFactory loggerFactory) : ILiveAccountManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ILiveConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly BinanceLiveOptions _options = options.Value;
    private readonly IOrderValidator _orderValidator = orderValidator;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    internal Func<string, BinanceAccountConfig, ILiveConnector>? ConnectorFactory;

    public async Task<ILiveConnector> GetOrCreateAsync(string accountName, CancellationToken ct = default)
    {
        // Fast path: return existing Running connector without lock
        if (_connectors.TryGetValue(accountName, out var existing) && existing.Status == LiveSessionStatus.Running)
            return existing;

        // Validate before acquiring lock
        if (!_options.Accounts.TryGetValue(accountName, out var accountConfig))
            throw new ArgumentException($"Binance account '{accountName}' is not configured. Check BinanceLive:Accounts in appsettings.json.");

        var semaphore = _accountLocks.GetOrAdd(accountName, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            // Double-check inside lock
            if (_connectors.TryGetValue(accountName, out existing))
            {
                if (existing.Status == LiveSessionStatus.Running)
                    return existing;

                // Old connector exists but not Running — dispose and replace
                await existing.DisposeAsync();
                _connectors.TryRemove(accountName, out _);
            }

            var connector = ConnectorFactory is not null
                ? ConnectorFactory(accountName, accountConfig)
                : new BinanceLiveConnector(
                    accountName,
                    accountConfig,
                    _options,
                    _orderValidator,
                    _loggerFactory.CreateLogger<BinanceLiveConnector>());

            await connector.ConnectAsync(ct);
            _connectors[accountName] = connector;
            return connector;
        }
        finally
        {
            semaphore.Release();

            // Evict idle lock if no connector exists (prevent unbounded growth)
            if (!_connectors.ContainsKey(accountName))
                _accountLocks.TryRemove(accountName, out _);
        }
    }

    public ILiveConnector? Get(string accountName) => _connectors.GetValueOrDefault(accountName);

    public IReadOnlyList<string> GetActiveAccountNames() =>
        _connectors.Where(kv => kv.Value.Status == LiveSessionStatus.Running)
            .Select(kv => kv.Key)
            .ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var connector in _connectors.Values)
            await connector.DisposeAsync();

        _connectors.Clear();
    }
}
