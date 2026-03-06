using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.Live.Binance;

public sealed class BinanceLiveConnector : ILiveConnector
{
    private readonly BinanceAccountConfig _accountConfig;
    private readonly BinanceLiveOptions _sharedOptions;
    private readonly IOrderValidator _orderValidator;
    private readonly ILogger<BinanceLiveConnector> _logger;

    private CancellationTokenSource? _cts;
    private BinanceApiClient? _apiClient;
    private BinanceWebSocketManager? _wsManager;

    private readonly ConcurrentDictionary<Guid, LiveSessionEntry> _sessions = new();
    private readonly ConcurrentDictionary<long, Guid> _binanceOrderToSession = new();

    public string AccountName { get; }
    public LiveSessionStatus Status { get; private set; } = LiveSessionStatus.Idle;
    public int SessionCount => _sessions.Count;

    private sealed record LiveSessionEntry(
        Guid SessionId,
        IInt64BarStrategy Strategy,
        LiveOrderContext OrderContext,
        IList<DataSubscription> Subscriptions,
        LiveEventRouting Routing,
        Asset PrimaryAsset)
    {
        public Channel<Action> EventQueue { get; } = Channel.CreateUnbounded<Action>(
            new UnboundedChannelOptions { SingleReader = true });
        public Task? ProcessingTask { get; set; }
    }

    public BinanceLiveConnector(
        string accountName,
        BinanceAccountConfig accountConfig,
        BinanceLiveOptions sharedOptions,
        IOrderValidator orderValidator,
        ILogger<BinanceLiveConnector> logger)
    {
        AccountName = accountName;
        _accountConfig = accountConfig;
        _sharedOptions = sharedOptions;
        _orderValidator = orderValidator;
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

            _wsManager = new BinanceWebSocketManager(
                _accountConfig.MarketStreamUrl,
                _sharedOptions.ReconnectDelay, _sharedOptions.MaxReconnectAttempts,
                _logger);
            _wsManager.Start(_cts);

            // Subscribe to user data via WebSocket API — awaited so we know it's active
            await _wsManager.ConnectUserDataWsApi(
                _accountConfig.WebSocketApiUrl, _accountConfig.ApiKey,
                _apiClient.Sign, OnExecutionReport);

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

    public async Task AddSessionAsync(LiveSessionConfig config, CancellationToken ct = default)
    {
        if (Status != LiveSessionStatus.Running)
            throw new InvalidOperationException($"Connector for account '{AccountName}' is not running.");

        var asset = config.PrimaryAsset;

        // Validate InitialCash against actual Binance account balance
        var symbolInfo = await _apiClient!.GetExchangeInfoAsync(asset.Name, ct);
        var accountInfo = await _apiClient.GetAccountInfoAsync(ct);

        var quoteBalance = accountInfo.Balances
            .FirstOrDefault(b => b.Asset.Equals(symbolInfo.QuoteAsset, StringComparison.OrdinalIgnoreCase));
        var freeBalance = quoteBalance is not null
            ? decimal.Parse(quoteBalance.Free, CultureInfo.InvariantCulture)
            : 0m;
        var availableScaled = (long)(freeBalance / asset.TickSize);

        if (config.InitialCash > availableScaled)
        {
            var requestedDecimal = config.InitialCash * asset.TickSize;
            throw new InvalidOperationException(
                $"Insufficient {symbolInfo.QuoteAsset} balance on account '{AccountName}'. " +
                $"Requested: {requestedDecimal:G29} {symbolInfo.QuoteAsset}, " +
                $"Available: {freeBalance:G29} {symbolInfo.QuoteAsset}.");
        }

        var portfolio = new Portfolio { InitialCash = config.InitialCash };
        portfolio.Initialize();

        var orderContext = new LiveOrderContext(
            portfolio, asset, _orderValidator, _logger, _apiClient!,
            config.SessionId, _binanceOrderToSession);
        orderContext.Start(_cts!.Token);

        // Set event bus on strategy if supported
        if (config.Strategy is IEventBusReceiver receiver)
            receiver.SetEventBus(NullEventBus.Instance);

        config.Strategy.OnInit();

        var entry = new LiveSessionEntry(
            config.SessionId,
            config.Strategy,
            orderContext,
            config.Subscriptions,
            config.Routing,
            asset);

        _sessions.TryAdd(config.SessionId, entry);

        // Start event processing loop for this session
        entry.ProcessingTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var action in entry.EventQueue.Reader.ReadAllAsync(_cts!.Token))
                {
                    try { action(); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in session {SessionId} event callback", entry.SessionId);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        // Subscribe to kline streams for each data subscription
        foreach (var sub in config.Subscriptions)
        {
            var symbol = sub.Asset.Name;
            var interval = MapTimeFrameToInterval(sub.TimeFrame);
            var capturedSub = sub;
            var capturedEntry = entry;

            _ = Task.Factory.StartNew(
                () => _wsManager!.SubscribeKline(symbol, interval, msg => OnKlineMessage(msg, capturedSub, capturedEntry)),
                _cts!.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        _logger.LogInformation(
            "Session {SessionId} added to account '{Account}' for {Asset} with {SubCount} subscription(s)",
            config.SessionId, AccountName, asset.Name, config.Subscriptions.Count);
    }

    public async Task RemoveSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(sessionId, out var entry))
            return;

        // Cancel pending orders via API
        foreach (var order in entry.OrderContext.GetPendingOrders())
            entry.OrderContext.Cancel(order.Id);

        // Drain event queue before stopping
        entry.EventQueue.Writer.TryComplete();
        if (entry.ProcessingTask is not null)
        {
            try { await entry.ProcessingTask; }
            catch (OperationCanceledException) { }
        }

        await entry.OrderContext.StopAsync();

        _logger.LogInformation("Session {SessionId} removed from account '{Account}'", sessionId, AccountName);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Status is LiveSessionStatus.Stopped or LiveSessionStatus.Stopping)
            return;

        Status = LiveSessionStatus.Stopping;
        _logger.LogInformation("Stopping connector for account '{Account}'", AccountName);

        try
        {
            // Stop all sessions
            foreach (var entry in _sessions.Values)
            {
                foreach (var order in entry.OrderContext.GetPendingOrders())
                    entry.OrderContext.Cancel(order.Id);

                entry.EventQueue.Writer.TryComplete();
                if (entry.ProcessingTask is not null)
                {
                    try { await entry.ProcessingTask; }
                    catch (OperationCanceledException) { }
                }

                await entry.OrderContext.StopAsync();
            }
            _sessions.Clear();

            _cts?.Cancel();

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

    private void OnKlineMessage(BinanceKlineMessage msg, DataSubscription subscription, LiveSessionEntry entry)
    {
        if (!msg.Kline.IsClosed)
            return;

        var asset = subscription.Asset;
        var tickSize = asset.TickSize;

        var bar = new Int64Bar(
            msg.Kline.OpenTime,
            (long)(decimal.Parse(msg.Kline.Open, CultureInfo.InvariantCulture) / tickSize),
            (long)(decimal.Parse(msg.Kline.High, CultureInfo.InvariantCulture) / tickSize),
            (long)(decimal.Parse(msg.Kline.Low, CultureInfo.InvariantCulture) / tickSize),
            (long)(decimal.Parse(msg.Kline.Close, CultureInfo.InvariantCulture) / tickSize),
            (long)decimal.Parse(msg.Kline.Volume, CultureInfo.InvariantCulture));

        entry.EventQueue.Writer.TryWrite(() =>
        {
            entry.OrderContext.ClearRecentFills();

            if (entry.Routing.HasFlag(LiveEventRouting.OnBarStart))
            {
                var startBar = new Int64Bar(bar.TimestampMs, bar.Open, bar.Open, bar.Open, bar.Open, 0);
                entry.Strategy.OnBarStart(startBar, subscription, entry.OrderContext);
            }

            if (entry.Routing.HasFlag(LiveEventRouting.OnBarComplete))
                entry.Strategy.OnBarComplete(bar, subscription, entry.OrderContext);
        });

        _logger.LogDebug("Bar closed {Symbol} {Interval}: O={Open} H={High} L={Low} C={Close}",
            msg.Symbol, msg.Kline.Interval, msg.Kline.Open, msg.Kline.High, msg.Kline.Low, msg.Kline.Close);
    }

    private void OnExecutionReport(BinanceExecutionReport report)
    {
        // Look up session via order routing map
        if (!_binanceOrderToSession.TryGetValue(report.OrderId, out var sessionId))
        {
            _logger.LogWarning(
                "Received execution report for unknown order {OrderId} (type={ExecType}) — may be a manual order on Binance",
                report.OrderId, report.ExecutionType);
            return;
        }

        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            _logger.LogWarning(
                "Received execution report for order {OrderId} but session {SessionId} no longer exists",
                report.OrderId, sessionId);
            return;
        }

        switch (report.ExecutionType)
        {
            case "TRADE":
                HandleTradeExecution(report, entry);
                break;

            case "CANCELED":
            case "EXPIRED":
                HandleOrderTermination(report, entry, OrderStatus.Cancelled);
                break;

            case "REJECTED":
                HandleOrderTermination(report, entry, OrderStatus.Rejected);
                break;
        }
    }

    private void HandleTradeExecution(BinanceExecutionReport report, LiveSessionEntry entry)
    {
        var asset = entry.PrimaryAsset;
        var tickSize = asset.TickSize;

        // Parse outside the callback for efficiency
        var fillPrice = (long)(decimal.Parse(report.LastFilledPrice, CultureInfo.InvariantCulture) / tickSize);
        var fillQty = decimal.Parse(report.LastFilledQty, CultureInfo.InvariantCulture);
        var commission = (long)(decimal.Parse(report.Commission, CultureInfo.InvariantCulture) / tickSize);
        var side = report.Side == "BUY" ? OrderSide.Buy : OrderSide.Sell;

        entry.EventQueue.Writer.TryWrite(() =>
        {
            var fill = new Fill(
                report.OrderId,
                asset,
                DateTimeOffset.FromUnixTimeMilliseconds(report.TransactionTime),
                fillPrice,
                fillQty,
                side,
                commission);

            entry.OrderContext.AddFill(fill);

            // Update pending order status based on Binance order status
            var pendingOrder = entry.OrderContext.GetPendingOrder(report.OrderId);
            if (pendingOrder is not null)
            {
                if (report.OrderStatus == "FILLED")
                {
                    pendingOrder.Status = OrderStatus.Filled;
                    entry.OrderContext.RemovePendingOrder(report.OrderId);
                }
                else if (report.OrderStatus == "PARTIALLY_FILLED")
                {
                    pendingOrder.Status = OrderStatus.PartiallyFilled;
                }
            }

            if (entry.Routing.HasFlag(LiveEventRouting.OnTrade))
            {
                var order = pendingOrder ?? new Order
                {
                    Id = report.OrderId,
                    Asset = asset,
                    Side = side,
                    Type = MapBinanceOrderType(report.OrderType),
                    Quantity = decimal.Parse(report.OriginalQuantity, CultureInfo.InvariantCulture),
                };

                entry.Strategy.OnTrade(fill, order, entry.OrderContext);
            }

            _logger.LogInformation(
                "Trade execution: {Side} {Qty} {Symbol} @ {Price} (status={Status}, session={SessionId})",
                report.Side, report.LastFilledQty, report.Symbol, report.LastFilledPrice,
                report.OrderStatus, entry.SessionId);
        });
    }

    private void HandleOrderTermination(BinanceExecutionReport report, LiveSessionEntry entry, OrderStatus terminalStatus)
    {
        entry.EventQueue.Writer.TryWrite(() =>
        {
            var pendingOrder = entry.OrderContext.GetPendingOrder(report.OrderId);
            if (pendingOrder is not null)
            {
                pendingOrder.Status = terminalStatus;
                entry.OrderContext.RemovePendingOrder(report.OrderId);
            }

            _binanceOrderToSession.TryRemove(report.OrderId, out _);
        });

        _logger.LogInformation(
            "Order {OrderId} terminated: {ExecType} → {Status} (session={SessionId})",
            report.OrderId, report.ExecutionType, terminalStatus, entry.SessionId);
    }

    private static string MapTimeFrameToInterval(TimeSpan timeFrame) => timeFrame.TotalMinutes switch
    {
        1 => "1m",
        3 => "3m",
        5 => "5m",
        15 => "15m",
        30 => "30m",
        60 => "1h",
        120 => "2h",
        240 => "4h",
        360 => "6h",
        480 => "8h",
        720 => "12h",
        1440 => "1d",
        4320 => "3d",
        10080 => "1w",
        _ => throw new ArgumentException($"Unsupported timeframe: {timeFrame}"),
    };

    private static OrderType MapBinanceOrderType(string type) => type switch
    {
        "MARKET" => OrderType.Market,
        "LIMIT" => OrderType.Limit,
        "STOP_LOSS" => OrderType.Stop,
        "STOP_LOSS_LIMIT" => OrderType.StopLimit,
        _ => OrderType.Market,
    };

    public async ValueTask DisposeAsync()
    {
        if (Status is not LiveSessionStatus.Stopped)
            await StopAsync();
    }
}
