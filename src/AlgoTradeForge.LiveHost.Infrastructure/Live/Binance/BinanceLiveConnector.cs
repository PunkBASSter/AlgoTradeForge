using System.Globalization;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

/// <remarks>
/// State machine invariant: <c>_apiClient</c>, <c>_reconciler</c>, <c>_source</c> and
/// <c>_router</c> are set in <c>ConnectAsync</c> before <c>Status</c> transitions to
/// <c>Running</c>. All methods that use the <c>!</c> forms are only reachable after that
/// transition (guarded by Status checks or by being callbacks from subsystems started during
/// <c>ConnectAsync</c>).
///
/// Composition root: the connector owns the WS/user-data lifecycle, reconciliation timer and a
/// per-session registry, but delegates the order side to <c>IOrderRouter</c> + <c>IAccountTarget</c>
/// and the data side to <c>IMarketDataSource</c>. Order→session resolution lives in the router.
/// </remarks>
public sealed class BinanceLiveConnector : ILiveConnector
{
    private readonly BinanceAccountConfig _accountConfig;
    private readonly BinanceLiveOptions _sharedOptions;
    private readonly IOrderValidator _orderValidator;
    private readonly ITickRouter _tickRouter;
    private readonly IStrategyDispatch _dispatch;
    private readonly ILogger<BinanceLiveConnector> _logger;

    private CancellationTokenSource? _cts;
    private BinanceApiClient? _apiClient;
    private BinanceWebSocketManager? _wsManager;

    // Order + data seams, built internally in ConnectAsync (they depend on _apiClient).
    private IMarketDataSource? _source;
    private IOrderRouter? _router;
    private BinanceAccountFundsSource? _fundsSource;
    private BinanceAccountTargetFactory? _factory;

    private OrderGroupReconciler? _reconciler;
    private LiveSessionDispatcher? _dispatcher;

    // Cache fields are accessed from multiple threads (snapshot requests + kline callbacks).
    // decimal (128-bit) and DateTimeOffset are not atomically readable on x64, so we
    // protect all reads/writes with _cacheLock to prevent torn reads.
    private readonly Lock _cacheLock = new();
    private decimal _cachedQuoteBalance;
    private DateTimeOffset _balanceCacheExpiry;

    private IReadOnlyList<ExchangeTradeDto> _cachedTrades = [];
    private DateTimeOffset _tradeCacheExpiry;

    public string AccountName { get; }
    public LiveSessionStatus Status { get; private set; } = LiveSessionStatus.Idle;
    public int SessionCount => _dispatcher?.SessionIds.Count ?? 0;

    public BinanceLiveConnector(
        string accountName,
        BinanceAccountConfig accountConfig,
        BinanceLiveOptions sharedOptions,
        IOrderValidator orderValidator,
        ITickRouter tickRouter,
        IStrategyDispatch dispatch,
        ILogger<BinanceLiveConnector> logger)
    {
        AccountName = accountName;
        _accountConfig = accountConfig;
        _sharedOptions = sharedOptions;
        _orderValidator = orderValidator;
        _tickRouter = tickRouter;
        _dispatch = dispatch;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Status = LiveSessionStatus.Connecting;
        _cts = new CancellationTokenSource();

        try
        {
            if (string.IsNullOrEmpty(_accountConfig.ApiKey) || string.IsNullOrEmpty(_accountConfig.ApiSecret))
            {
                throw new InvalidOperationException(
                    $"API credentials are not configured for account '{AccountName}'. " +
                    $"Set BinanceLive:Accounts:{AccountName}:ApiKey and BinanceLive:Accounts:{AccountName}:ApiSecret.");
            }

            _apiClient = new BinanceApiClient(
                _accountConfig.RestUrl, _accountConfig.ApiKey, _accountConfig.ApiSecret, _logger);

            // Sync local clock with Binance server to avoid timestamp rejection
            await _apiClient.SyncTimeAsync(ct);

            // Build the order + data seams now that _apiClient exists. The factory discovers
            // funds lazily per account at ResolveTarget time; the execution asset is threaded
            // per-session into ResolveTarget.
            _source = new BinanceMarketDataSource(_dispatch, _tickRouter);
            _fundsSource = new BinanceAccountFundsSource(_apiClient);
            _factory = new BinanceAccountTargetFactory(
                _fundsSource, _apiClient, _orderValidator, _logger,
                _sharedOptions.LiveChannelCapacity);
            _router = new OrderRouter(_factory, NullLogger<OrderRouter>.Instance);

            _wsManager = new BinanceWebSocketManager(
                _accountConfig.MarketStreamUrl,
                _sharedOptions.ReconnectDelay, _sharedOptions.MaxReconnectAttempts,
                _logger);
            _wsManager.Start(_cts);

            // Subscribe to user data via WebSocket API — awaited so we know it's active
            await _wsManager.ConnectUserDataWsApi(
                _accountConfig.WebSocketApiUrl, _accountConfig.ApiKey,
                _apiClient.Sign, _apiClient.GetTimestamp, OnExecutionReport);

            _reconciler = new OrderGroupReconciler(_apiClient, _logger);

            _dispatcher = new LiveSessionDispatcher(
                _router, _source, _dispatch, _reconciler,
                new LiveDispatcherOptions(
                    _sharedOptions.LiveChannelCapacity,
                    _sharedOptions.MarketDataChannelCapacity,
                    _sharedOptions.ReconciliationInterval),
                _logger);
            _dispatcher.Start(_cts.Token);
            _dispatcher.StartReconciliation();

            Status = LiveSessionStatus.Running;
            _logger.LogInformation(
                "Connector for account '{Account}' connected. REST={RestUrl}",
                AccountName, _accountConfig.RestUrl);
        }
        catch (Exception ex)
        {
            Status = LiveSessionStatus.Error;
            _logger.LogError(ex, "Failed to connect account '{Account}'", AccountName);
            throw;
        }
    }

    internal async Task<decimal> GetTickerPriceAsync(string symbol, CancellationToken ct = default)
    {
        if (_apiClient is null)
            throw new InvalidOperationException("Connector is not connected.");
        return await _apiClient.GetTickerPriceAsync(symbol, ct);
    }

    public async Task AddSessionAsync(LiveSessionConfig config, CancellationToken ct = default)
    {
        if (Status != LiveSessionStatus.Running)
            throw new InvalidOperationException($"Connector for account '{AccountName}' is not running.");

        var asset = config.ExecutionAsset;

        // Venue-specific quote-asset resolution stays in the connector; the dispatcher owns the
        // venue-neutral session lifecycle from co-tenancy fence onward.
        var symbolInfo = await _apiClient!.GetExchangeInfoAsync(asset.Name, ct);

        await _dispatcher!.AddSession(config, symbolInfo.QuoteAsset, ct);
    }

    public async Task RemoveSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (_dispatcher is not null)
            await _dispatcher.RemoveSession(sessionId, ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Status is LiveSessionStatus.Stopped or LiveSessionStatus.Stopping)
            return;

        Status = LiveSessionStatus.Stopping;
        _logger.LogInformation("Stopping connector for account '{Account}'", AccountName);

        // Snapshot the live session ids BEFORE Stop() clears the dispatcher's table — the safety-net
        // cancel-all below needs the symbols those sessions traded.
        var sessionAssets = _dispatcher is not null
            ? _dispatcher.SessionIds
                .Select(id => _dispatcher.TryGetSessionData(id, out var data) ? data.ExecutionAsset.Name : null)
                .Where(name => name is not null)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];

        try
        {
            // Dispatcher steps 1-4 + 6: drain sessions → dispose router (cancels-all) → cancel CTS →
            // await reconcile → clear sessions.
            if (_dispatcher is not null)
                await _dispatcher.Stop(ct);

            // 3. Now cancel CTS — stops WebSocket/kline loops (dispatcher cancelled its own).
            _cts?.Cancel();

            // 5. Safety-net: cancel all open orders on exchange per symbol (belt-and-suspenders,
            //    covers multi-symbol accounts even though the target dispose already cancelled).
            if (_apiClient is not null)
            {
                foreach (var symbol in sessionAssets)
                {
                    try
                    {
                        await _apiClient.CancelAllOpenOrdersAsync(symbol!);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Safety-net cancel-all failed for {Symbol}", symbol);
                    }
                }
            }

            // 6. Transport teardown.
            if (_wsManager is not null)
                await _wsManager.DisposeAsync();

            _apiClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping connector for account '{Account}'", AccountName);
        }
        finally
        {
            Status = LiveSessionStatus.Stopped;
            _logger.LogInformation("Connector for account '{Account}' stopped", AccountName);
        }
    }

    // Venue mapping: WS user-data → neutral ExecutionReport, dispatched by the dispatcher. The
    // dispatcher buffers unmapped orders by id, so an unresolved asset (seeded from the report's own
    // symbol) is fine — the asset is re-derived from the session on replay anyway.
    private void OnExecutionReport(BinanceExecutionReport report)
    {
        // Placeholder asset for unmapped orders is never scaled — the dispatcher overwrites it with
        // the session's execution asset once the order resolves (see OnExecutionReport re-stamp).
        var asset = _dispatcher!.TryResolveAsset(report.OrderId, out var resolved)
            ? resolved
            : ResolveAssetForSymbol(report.Symbol);

        _dispatcher.OnExecutionReport(MapToNeutral(report, asset));
    }

    private static ExecutionReport MapToNeutral(BinanceExecutionReport report, Asset asset)
    {
        var scale = new ScaleContext(asset);
        var execType = report.ExecutionType switch
        {
            "TRADE" => ExecType.Trade,
            "CANCELED" => ExecType.Canceled,
            "EXPIRED" => ExecType.Expired,
            "REJECTED" => ExecType.Rejected,
            _ => ExecType.New,
        };
        var status = report.OrderStatus switch
        {
            "FILLED" => OrderStatus.Filled,
            "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "CANCELED" => OrderStatus.Cancelled,
            "REJECTED" => OrderStatus.Rejected,
            "EXPIRED" => OrderStatus.Cancelled,
            _ => OrderStatus.Pending,
        };

        return new ExecutionReport(
            report.OrderId,
            asset,
            report.Side == "BUY" ? OrderSide.Buy : OrderSide.Sell,
            execType,
            decimal.Parse(report.LastFilledPrice, CultureInfo.InvariantCulture),
            decimal.Parse(report.LastFilledQty, CultureInfo.InvariantCulture),
            decimal.Parse(report.Commission, CultureInfo.InvariantCulture),
            status,
            DateTimeOffset.FromUnixTimeMilliseconds(report.TransactionTime),
            ParseBinanceOrderType(report.OrderType),
            decimal.Parse(report.OriginalQuantity, CultureInfo.InvariantCulture));
    }

    private static OrderType ParseBinanceOrderType(string type) => type switch
    {
        "MARKET" => OrderType.Market,
        "LIMIT" => OrderType.Limit,
        "STOP_LOSS" => OrderType.Stop,
        "STOP_LOSS_LIMIT" => OrderType.StopLimit,
        _ => OrderType.Market,
    };

    // Placeholder asset for an unmapped order (no session yet to resolve from). The dispatcher
    // re-stamps the session's real execution asset once the order maps, so this value is only a
    // non-null carrier and is never used for scaling.
    private static Asset ResolveAssetForSymbol(string symbol) =>
        CryptoAsset.Create(symbol, "Binance", decimalDigits: 8);

    internal async Task<LiveSessionSnapshot?> GetSessionSnapshotAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (_dispatcher is null || !_dispatcher.TryGetSessionData(sessionId, out var data))
            return null;

        // Bars + last-bar-per-subscription come from the data-plane bar sources' Recent rings.
        var barFields = SessionSnapshotBars.Build(
            data.Subscriptions.ToList(),
            _source!.RecentBars);

        var exchangeBalance = await GetCachedQuoteBalanceAsync(data.QuoteAsset, ct);
        var exchangeTrades = await GetCachedTradesAsync(data.ExecutionAsset.Name, ct);

        var ctx = data.OrderContext;
        return new LiveSessionSnapshot(
            barFields.Bars,
            ctx.GetAllFills(),
            ctx.GetPendingOrders(),
            ctx.GetPositions(),
            ctx.Cash,
            ctx.Portfolio.InitialCash,
            exchangeBalance,
            data.ExecutionAsset,
            data.Subscriptions.ToList(),
            barFields.LastBarsPerSubscription,
            exchangeTrades);
    }

    private async Task<decimal> GetCachedQuoteBalanceAsync(string quoteAsset, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (DateTimeOffset.UtcNow < _balanceCacheExpiry)
                return _cachedQuoteBalance;
        }

        try
        {
            var accountInfo = await _apiClient!.GetAccountInfoAsync(ct);
            var balance = accountInfo.Balances
                .FirstOrDefault(b => b.Asset.Equals(quoteAsset, StringComparison.OrdinalIgnoreCase));

            lock (_cacheLock)
            {
                _cachedQuoteBalance = balance is not null
                    ? decimal.Parse(balance.Free, CultureInfo.InvariantCulture)
                    : 0m;
                _balanceCacheExpiry = DateTimeOffset.UtcNow.AddSeconds(15);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh exchange balance for {QuoteAsset}", quoteAsset);
        }

        lock (_cacheLock)
            return _cachedQuoteBalance;
    }

    private async Task<IReadOnlyList<ExchangeTradeDto>> GetCachedTradesAsync(string symbol, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (DateTimeOffset.UtcNow < _tradeCacheExpiry)
                return _cachedTrades;
        }

        try
        {
            var trades = await _apiClient!.GetMyTradesAsync(symbol, 50, ct);
            var result = trades
                .Select(t => new ExchangeTradeDto(
                    t.OrderId,
                    DateTimeOffset.FromUnixTimeMilliseconds(t.Time).ToString("O"),
                    decimal.Parse(t.Price, CultureInfo.InvariantCulture),
                    decimal.Parse(t.Qty, CultureInfo.InvariantCulture),
                    t.IsBuyer ? "Buy" : "Sell",
                    decimal.Parse(t.Commission, CultureInfo.InvariantCulture),
                    t.CommissionAsset))
                .ToList();

            lock (_cacheLock)
            {
                _cachedTrades = result;
                _tradeCacheExpiry = DateTimeOffset.UtcNow.AddSeconds(15);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch exchange trades for {Symbol}", symbol);
        }

        lock (_cacheLock)
            return _cachedTrades;
    }

    public async ValueTask DisposeAsync()
    {
        if (Status is not LiveSessionStatus.Stopped)
            await StopAsync();
    }
}
