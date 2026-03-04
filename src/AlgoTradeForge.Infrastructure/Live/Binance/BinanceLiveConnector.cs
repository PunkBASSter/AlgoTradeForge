using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.Live.Binance;

public sealed class BinanceLiveConnector : ILiveConnector
{
    private readonly BinanceLiveOptions _options;
    private readonly IOrderValidator _orderValidator;
    private readonly ILogger<BinanceLiveConnector> _logger;

    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private BinanceApiClient? _apiClient;
    private BinanceWebSocketManager? _wsManager;
    private LiveOrderContext? _orderContext;
    private LiveSessionConfig? _config;
    private Timer? _listenKeyTimer;

    public Guid SessionId { get; private set; }
    public LiveSessionStatus Status { get; private set; } = LiveSessionStatus.Idle;

    public BinanceLiveConnector(
        IOptions<BinanceLiveOptions> options,
        IOrderValidator orderValidator,
        ILogger<BinanceLiveConnector> logger)
    {
        _options = options.Value;
        _orderValidator = orderValidator;
        _logger = logger;
    }

    public async Task StartAsync(LiveSessionConfig config, CancellationToken ct = default)
    {
        _config = config;
        SessionId = config.SessionId;
        Status = LiveSessionStatus.Connecting;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var paper = config.PaperTrading;
        var mode = paper ? "PAPER (testnet)" : "LIVE";

        try
        {
            // Resolve effective URLs and credentials based on trading mode
            var restUrl = _options.GetRestUrl(paper);
            var apiKey = _options.GetApiKey(paper);
            var apiSecret = _options.GetApiSecret(paper);

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
            {
                var label = paper ? "Testnet" : "Production";
                throw new InvalidOperationException(
                    $"{label} API credentials are not configured. " +
                    $"Set BinanceLive:{(paper ? "Testnet" : "")}ApiKey and BinanceLive:{(paper ? "Testnet" : "")}ApiSecret.");
            }

            _apiClient = new BinanceApiClient(restUrl, apiKey, apiSecret, _logger);

            // Initialize portfolio
            var initialCashScaled = (long)(config.InitialCash / config.PrimaryAsset.TickSize);
            var portfolio = new Portfolio { InitialCash = initialCashScaled };
            portfolio.Initialize();

            _orderContext = new LiveOrderContext(
                portfolio,
                config.PrimaryAsset,
                _orderValidator,
                _logger,
                _apiClient);

            // Set event bus on strategy if supported
            if (config.Strategy is IEventBusReceiver receiver)
                receiver.SetEventBus(NullEventBus.Instance);

            // Initialize strategy
            config.Strategy.OnInit();

            // Set up WebSocket manager
            // Market data always from production (testnet streams are unreliable)
            // User data stream goes to testnet when paper trading
            var marketDataWsUrl = _options.GetMarketDataWsUrl(paper);
            var userDataWsUrl = _options.GetUserDataWsUrl(paper);

            _wsManager = new BinanceWebSocketManager(
                marketDataWsUrl, userDataWsUrl,
                _options.ReconnectDelay, _options.MaxReconnectAttempts,
                _logger);
            _wsManager.Start(_cts);

            // Subscribe to kline streams for each data subscription
            foreach (var sub in config.Subscriptions)
            {
                var symbol = sub.Asset.Name;
                var interval = MapTimeFrameToInterval(sub.TimeFrame);
                var capturedSub = sub;

                _ = Task.Factory.StartNew(
                    () => _wsManager.SubscribeKline(symbol, interval, msg => OnKlineMessage(msg, capturedSub)),
                    _cts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            // Subscribe to user data stream (both live and paper — testnet has real order matching)
            var listenKey = await _apiClient.CreateListenKeyAsync(ct);

            _ = Task.Factory.StartNew(
                () => _wsManager.SubscribeUserData(listenKey, OnExecutionReport),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            // Start listenKey keepalive timer
            _listenKeyTimer = new Timer(
                async _ =>
                {
                    try
                    {
                        if (_apiClient is not null)
                            await _apiClient.KeepAliveListenKeyAsync(listenKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to refresh listenKey");
                    }
                },
                null,
                _options.ListenKeyRefreshInterval,
                _options.ListenKeyRefreshInterval);

            Status = LiveSessionStatus.Running;

            _logger.LogInformation(
                "Live session {SessionId} started ({Mode}) for {Asset} with {SubCount} subscription(s). REST={RestUrl}",
                config.SessionId, mode, config.PrimaryAsset.Name, config.Subscriptions.Count, restUrl);
        }
        catch (Exception ex)
        {
            Status = LiveSessionStatus.Error;
            _logger.LogError(ex, "Failed to start live session {SessionId}", config.SessionId);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Status is LiveSessionStatus.Stopped or LiveSessionStatus.Stopping)
            return;

        Status = LiveSessionStatus.Stopping;
        _logger.LogInformation("Stopping live session {SessionId}", SessionId);

        try
        {
            // Cancel all pending orders
            if (_orderContext is not null)
            {
                foreach (var order in _orderContext.GetPendingOrders())
                    _orderContext.Cancel(order.Id);
            }

            // Stop timers and cancel tasks
            if (_listenKeyTimer is not null)
                await _listenKeyTimer.DisposeAsync();

            _cts?.Cancel();

            if (_wsManager is not null)
                await _wsManager.DisposeAsync();

            _apiClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping live session {SessionId}", SessionId);
        }
        finally
        {
            Status = LiveSessionStatus.Stopped;
            _logger.LogInformation("Live session {SessionId} stopped", SessionId);
        }
    }

    private void OnKlineMessage(BinanceKlineMessage msg, DataSubscription subscription)
    {
        if (!msg.Kline.IsClosed)
            return;

        var config = _config!;
        var asset = subscription.Asset;
        var tickSize = asset.TickSize;

        var bar = new Int64Bar(
            msg.Kline.OpenTime,
            (long)(decimal.Parse(msg.Kline.Open) / tickSize),
            (long)(decimal.Parse(msg.Kline.High) / tickSize),
            (long)(decimal.Parse(msg.Kline.Low) / tickSize),
            (long)(decimal.Parse(msg.Kline.Close) / tickSize),
            (long)decimal.Parse(msg.Kline.Volume));

        lock (_lock)
        {
            _orderContext!.ClearRecentFills();

            if (config.Routing.HasFlag(LiveEventRouting.OnBarStart))
            {
                var startBar = new Int64Bar(bar.TimestampMs, bar.Open, bar.Open, bar.Open, bar.Open, 0);
                config.Strategy.OnBarStart(startBar, subscription, _orderContext);
            }

            if (config.Routing.HasFlag(LiveEventRouting.OnBarComplete))
                config.Strategy.OnBarComplete(bar, subscription, _orderContext);
        }

        _logger.LogDebug("Bar closed {Symbol} {Interval}: O={Open} H={High} L={Low} C={Close}",
            msg.Symbol, msg.Kline.Interval, msg.Kline.Open, msg.Kline.High, msg.Kline.Low, msg.Kline.Close);
    }

    private void OnExecutionReport(BinanceExecutionReport report)
    {
        if (report.ExecutionType != "TRADE")
            return;

        var config = _config!;
        var asset = config.PrimaryAsset;
        var tickSize = asset.TickSize;

        lock (_lock)
        {
            var fillPrice = (long)(decimal.Parse(report.LastFilledPrice) / tickSize);
            var fillQty = decimal.Parse(report.LastFilledQty);
            var commission = (long)(decimal.Parse(report.Commission) / tickSize);
            var side = report.Side == "BUY" ? OrderSide.Buy : OrderSide.Sell;

            var fill = new Fill(
                report.OrderId,
                asset,
                DateTimeOffset.FromUnixTimeMilliseconds(report.TransactionTime),
                fillPrice,
                fillQty,
                side,
                commission);

            _orderContext!.AddFill(fill);

            // Find matching pending order
            var pendingOrder = _orderContext.GetPendingOrder(report.OrderId);
            if (pendingOrder is not null && report.OrderStatus == "FILLED")
            {
                pendingOrder.Status = OrderStatus.Filled;
                _orderContext.RemovePendingOrder(report.OrderId);
            }

            if (config.Routing.HasFlag(LiveEventRouting.OnTrade))
            {
                var order = pendingOrder ?? new Order
                {
                    Id = report.OrderId,
                    Asset = asset,
                    Side = side,
                    Type = MapBinanceOrderType(report.OrderType),
                    Quantity = decimal.Parse(report.OriginalQuantity),
                };

                config.Strategy.OnTrade(fill, order, _orderContext);
            }

            _logger.LogInformation("Execution report: {Side} {Qty} {Symbol} @ {Price}",
                report.Side, report.LastFilledQty, report.Symbol, report.LastFilledPrice);
        }
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
