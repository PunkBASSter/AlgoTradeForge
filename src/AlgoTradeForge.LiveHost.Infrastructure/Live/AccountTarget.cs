using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// One broker account. Owns the shared Portfolio + account-scoped LiveOrderContext.
// Born running (the LiveOrderContext is Start()ed before/at construction); torn down via
// DisposeAsync (flush queued orders/cancels via LiveOrderContext.StopAsync, then cancel-all
// open orders on the exchange). Idempotent.
public sealed class AccountTarget : IAccountTarget
{
    private readonly LiveOrderContext _orderContext;
    private readonly IExchangeOrderClient _orderClient;
    private readonly IReadOnlyList<string> _symbolsToCancelOnDispose;
    private readonly ILogger _logger;
    private int _disposed;

    public string AccountName { get; }
    public Portfolio Portfolio { get; }

    public AccountTarget(
        string accountName,
        Portfolio portfolio,
        LiveOrderContext orderContext,
        IExchangeOrderClient orderClient,
        IEnumerable<string> symbolsToCancelOnDispose,
        ILogger logger)
    {
        AccountName = accountName;
        Portfolio = portfolio;
        _orderContext = orderContext;
        _orderClient = orderClient;
        _symbolsToCancelOnDispose = symbolsToCancelOnDispose.ToList();
        _logger = logger;
    }

    public IOrderContext OrderContextFor(Guid sessionId) => new SessionOrderContext(sessionId, _orderContext);

    internal LiveOrderContext OrderContext => _orderContext;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Graceful: flush queued orders/cancels first (StopAsync awaits the drain tasks).
        await _orderContext.StopAsync();

        foreach (var symbol in _symbolsToCancelOnDispose)
        {
            try { await _orderClient.CancelAllOpenOrdersAsync(symbol); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cancel-all on dispose failed for {Symbol} (account {Account})",
                    symbol, AccountName);
            }
        }
    }
}
