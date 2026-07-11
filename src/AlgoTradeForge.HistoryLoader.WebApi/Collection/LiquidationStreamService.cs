using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

internal sealed class LiquidationStreamService(
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ICollectionCircuitBreaker circuitBreaker,
    IHttpClientFactory httpClientFactory,
    ICollectionPlanSource planSource,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<LiquidationStreamService> logger) : BackgroundService
{
    private const string StreamPath = "/ws/!forceOrder@arr";
    private static readonly string[] Columns = ["side", "price", "qty", "notional_usd"];
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(5);
    private const int MaxReconnectAttempts = 10;
    private static readonly TimeSpan StableConnectionUptime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StatusFlushInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);

    private bool _planDirty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("LiquidationStreamService started");

        await EnsureSchemas(stoppingToken);

        Action onPlanChanged = () => Volatile.Write(ref _planDirty, true);
        planSource.PlanChanged += onPlanChanged;
        try
        {
            var reconnect = new StreamReconnectPolicy(
                MaxReconnectAttempts, InitialReconnectDelay, StableConnectionUptime);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (circuitBreaker.IsTripped)
                {
                    if (circuitBreaker.IsAutoResettable)
                    {
                        var probeInterval = options.CurrentValue.NetworkProbeIntervalSeconds;
                        logger.LogWarning(
                            "LiquidationStreamService paused — network unreachable, probing every {Interval}s",
                            probeInterval);

                        while (circuitBreaker.IsTripped && circuitBreaker.IsAutoResettable
                               && !stoppingToken.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(probeInterval), stoppingToken);
                            if (await ProbeConnectivityAsync(stoppingToken))
                            {
                                circuitBreaker.Reset();
                                logger.LogInformation("LiquidationStreamService — connectivity restored");
                                break;
                            }
                        }
                    }
                    else
                    {
                        var cooldown = options.CurrentValue.CircuitBreakerCooldownMinutes;
                        logger.LogWarning(
                            "LiquidationStreamService paused — circuit breaker tripped, retrying in {Cooldown} min",
                            cooldown);
                        await Task.Delay(TimeSpan.FromMinutes(cooldown), stoppingToken);
                    }

                    continue;
                }

                try
                {
                    await ConnectAndStreamAsync(reconnect, stoppingToken);
                    // Normal disconnect — reset and reconnect.
                    reconnect.Reset();
                }
                catch (OperationCanceledException) when (
                    stoppingToken.IsCancellationRequested)
                {
                    // Real shutdown. HttpClient/WebSocket timeouts also throw OCE without caller
                    // cancellation; the qualifier ensures those fall through to the reconnect path.
                    break;
                }
                catch (Exception ex)
                {
                    var decision = reconnect.OnFailure();

                    if (NetworkErrorHelper.IsNetworkError(ex)
                        && decision.Attempt >= options.CurrentValue.NetworkFailureThreshold)
                    {
                        logger.LogError(
                            "LiquidationStreamService — network unreachable after {Count} attempts, tripping circuit breaker",
                            decision.Attempt);
                        circuitBreaker.Trip("Network unreachable (WebSocket)", TripReason.Network);
                        reconnect.Reset();
                        continue;
                    }

                    if (decision.GiveUp)
                    {
                        logger.LogCritical(ex,
                            "LiquidationStreamService exceeded {Max} reconnect attempts, stopping",
                            MaxReconnectAttempts);
                        break;
                    }

                    logger.LogWarning(ex,
                        "LiquidationStreamService disconnected (attempt {Attempt}/{Max}), reconnecting in {Delay}s",
                        decision.Attempt, MaxReconnectAttempts, decision.Delay.TotalSeconds);
                    await Task.Delay(decision.Delay, stoppingToken);
                }
            }
        }
        finally
        {
            planSource.PlanChanged -= onPlanChanged;
        }

        logger.LogInformation("LiquidationStreamService stopped");
    }

    private async Task<bool> ProbeConnectivityAsync(CancellationToken ct)
    {
        try
        {
            var baseUrl = options.CurrentValue.Binance.FuturesBaseUrl;
            using var client = httpClientFactory.CreateClient("connectivity-probe");
            client.Timeout = TimeSpan.FromSeconds(5);

            using var response = await client.GetAsync($"{baseUrl}/fapi/v1/ping", ct);
            return true;
        }
        catch (Exception ex) when (
            !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // HttpClient.Timeout throws TaskCanceledException (an OCE); without this qualifier
            // a timeout would escape and crash the BG service. Probe is best-effort.
            return false;
        }
    }

    private async Task ConnectAndStreamAsync(StreamReconnectPolicy reconnect, CancellationToken ct)
    {
        var config = options.CurrentValue;
        var url = $"{config.Binance.FuturesWsBaseUrl}{StreamPath}";

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        reconnect.OnConnected();
        logger.LogInformation("Connected to !forceOrder@arr stream at {Url}", url);

        await ReadLoopAsync(ws, ct);
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var messageStream = new MemoryStream();
        var statusTracker = new Dictionary<string, (long count, long? firstTs, long? lastTs)>();
        var lastStatusFlush = DateTimeOffset.UtcNow;
        var lastHeartbeat = DateTimeOffset.UtcNow;
        long totalReceived = 0;
        long totalWritten = 0;

        var enabledSymbols = BuildEnabledSymbolSet(planSource.Current);

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            // Plan changed — rebuild enabled-symbol set in place (no reconnect; stream is venue-wide).
            if (Volatile.Read(ref _planDirty))
            {
                Volatile.Write(ref _planDirty, false);
                enabledSymbols = BuildEnabledSymbolSet(planSource.Current);
                // Boot-time EnsureSchemas ran against CollectionPlan.Empty; re-run once the
                // real plan lands so schemas are pre-created for newly planned assets.
                await EnsureSchemas(ct);
                logger.LogInformation(
                    "LiquidationStreamService: plan changed, rebuilt enabled set ({Count} symbols)",
                    enabledSymbols.Count);
            }

            messageStream.SetLength(0);

            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogWarning("WebSocket server initiated close: {Status} {Description}",
                        result.CloseStatus, result.CloseStatusDescription);
                    return;
                }

                messageStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            try
            {
                totalReceived++;

                var parsed = ParseForceOrder(new ReadOnlyMemory<byte>(messageStream.GetBuffer(), 0, (int)messageStream.Length));
                if (parsed is null)
                    continue;

                var (symbol, record) = parsed.Value;

                if (!enabledSymbols.Contains(symbol))
                    continue;

                var asset = FindAsset(planSource.Current, symbol);
                if (asset is null)
                    continue;

                var assetDir = BackfillOrchestrator.ResolveAssetDir(
                    options.CurrentValue.DataRoot, asset);
                await schemaManager.EnsureSchema(assetDir, FeedNames.Liquidations, "", Columns, ct: ct);
                feedWriter.Write(assetDir, FeedNames.Liquidations, "", Columns, record);
                totalWritten++;

                var sideLabel = record.Values[0] > 0 ? "LONG" : "SHORT";
                logger.LogDebug(
                    "Liquidation {Symbol} {Side} qty={Qty} price={Price} notional=${Notional:F2}",
                    symbol, sideLabel, record.Values[2], record.Values[1], record.Values[3]);

                if (!statusTracker.TryGetValue(assetDir, out var st))
                    st = (0, null, null);
                st.count++;
                st.firstTs ??= record.TimestampMs;
                st.lastTs = record.TimestampMs;
                statusTracker[assetDir] = st;
            }
            catch (Exception ex) when (
                !(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                // Real shutdown propagates; a stray HTTP/WS timeout must not kill the stream loop.
                logger.LogError(ex, "Failed to process liquidation message");
            }

            var now = DateTimeOffset.UtcNow;

            if (now - lastStatusFlush >= StatusFlushInterval)
            {
                await FlushStatus(statusTracker, ct);
                lastStatusFlush = now;
            }

            if (now - lastHeartbeat >= HeartbeatInterval)
            {
                logger.LogInformation(
                    "LiquidationStream heartbeat — {Received} events received, {Written} written for tracked symbols",
                    totalReceived, totalWritten);
                lastHeartbeat = now;
            }
        }

        await FlushStatus(statusTracker, ct);
    }

    internal static (string Symbol, FeedRecord Record)? ParseForceOrder(ReadOnlyMemory<byte> data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (!root.TryGetProperty("e", out var eventType)
                || eventType.GetString() != "forceOrder")
                return null;

            if (!root.TryGetProperty("o", out var order))
                return null;

            // Only process FILLED orders.
            if (!order.TryGetProperty("X", out var status)
                || status.GetString() != "FILLED")
                return null;

            if (!order.TryGetProperty("s", out var symbolProp))
                return null;
            var symbol = symbolProp.GetString();
            if (string.IsNullOrEmpty(symbol))
                return null;

            if (!order.TryGetProperty("T", out var tsProp))
                return null;
            long timestamp = tsProp.ValueKind == JsonValueKind.Number
                ? tsProp.GetInt64()
                : long.Parse(tsProp.GetString()!, CultureInfo.InvariantCulture);

            // SELL = long liquidated (+1), BUY = short liquidated (-1).
            if (!order.TryGetProperty("S", out var sideProp))
                return null;
            var sideStr = sideProp.GetString();
            double side = sideStr == "SELL" ? 1.0 : sideStr == "BUY" ? -1.0 : double.NaN;
            if (double.IsNaN(side))
                return null;

            if (!order.TryGetProperty("ap", out var apProp))
                return null;
            var apStr = apProp.GetString();
            if (!double.TryParse(apStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var avgPrice))
                return null;

            if (!order.TryGetProperty("z", out var zProp))
                return null;
            var zStr = zProp.GetString();
            if (!double.TryParse(zStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var execQty))
                return null;

            double notionalUsd = execQty * avgPrice;

            return (symbol, new FeedRecord(timestamp, [side, avgPrice, execQty, notionalUsd]));
        }
        catch
        {
            return null;
        }
    }

    private async Task EnsureSchemas(CancellationToken ct)
    {
        var dataRoot = options.CurrentValue.DataRoot;
        foreach (var asset in planSource.Current.Assets)
        {
            if (!AssetTypes.IsFutures(asset.Venue.AssetType))
                continue;
            if (!asset.Feeds.Any(f => f.FeedName == FeedNames.Liquidations))
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(dataRoot, asset);
            await schemaManager.EnsureSchema(assetDir, FeedNames.Liquidations, "", Columns, ct: ct);
        }
    }

    private async Task FlushStatus(Dictionary<string, (long count, long? firstTs, long? lastTs)> tracker, CancellationToken ct)
    {
        foreach (var (assetDir, st) in tracker)
        {
            if (st.count == 0)
                continue;

            var existing = await feedStatusStore.Load(assetDir, FeedNames.Liquidations, "", ct);

            await feedStatusStore.Save(assetDir, FeedNames.Liquidations, "", new FeedStatus
            {
                FeedName = FeedNames.Liquidations,
                Interval = "",
                FirstTimestamp = existing?.FirstTimestamp ?? st.firstTs,
                LastTimestamp = st.lastTs,
                LastRunUtc = DateTimeOffset.UtcNow,
                RecordCount = (existing?.RecordCount ?? 0) + st.count,
                Health = CollectionHealth.Healthy
            }, ct);
        }

        tracker.Clear();
    }

    internal static HashSet<string> BuildEnabledSymbolSet(CollectionPlan plan) =>
        plan.Assets
            .Where(a => AssetTypes.IsFutures(a.Venue.AssetType))
            .Where(a => a.Feeds.Any(f => f.FeedName == FeedNames.Liquidations && f.Collect == "eager"))
            .Select(a => a.Venue.ApiSymbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static CollectionAsset? FindAsset(CollectionPlan plan, string symbol) =>
        plan.Assets.FirstOrDefault(a =>
            AssetTypes.IsFutures(a.Venue.AssetType)
            && string.Equals(a.Venue.ApiSymbol, symbol, StringComparison.OrdinalIgnoreCase));
}
