using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

/// <summary>
/// Real-time spot aggregate-trade collection via Binance Spot WebSocket combined streams.
/// Runs alongside the REST <c>TicksCollectorService</c>; the tick writer's <c>agg_id</c>
/// dedup makes the overlap idempotent and lets the REST poll close gaps during WS outages.
/// </summary>
internal sealed class SpotAggTradeStreamService(
    ITickFeedWriter tickWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ICollectionCircuitBreaker circuitBreaker,
    IHttpClientFactory httpClientFactory,
    ICollectionPlanSource planSource,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<SpotAggTradeStreamService> logger) : BackgroundService
{
    private static readonly string[] TickColumns = ["price", "qty", "is_buyer_maker", "agg_id"];
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(5);
    private const int MaxReconnectAttempts = 10;
    private static readonly TimeSpan StableConnectionUptime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StatusFlushInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    // A connected-but-silent WS (half-open connection) leaves ReceiveAsync blocked forever with no
    // error. Spot aggTrades on a liquid symbol arrive sub-second, so a minute of silence means the
    // connection is dead => close and reconnect (loud) instead of hanging.
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PlanPollInterval = TimeSpan.FromSeconds(1);

    private bool _planDirty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SpotAggTradeStreamService started");

        // Subscribe BEFORE the first plan read — at boot Current is CollectionPlan.Empty
        // until DesiredStateService publishes; an empty read must not exit the service.
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
                    await WaitForCircuitResetAsync(stoppingToken);
                    continue;
                }

                // Clear-then-read: a PlanChanged after this clear re-marks dirty and is seen
                // by the read loop; one before it is captured by the fresh Current read.
                Volatile.Write(ref _planDirty, false);
                var symbols = BuildEnabledSpotSymbols(planSource.Current);

                if (symbols.Count == 0)
                {
                    // No eligible symbols (plan may still be Empty at boot) — wait, don't exit.
                    await WaitForPlanChangeAsync(stoppingToken);
                    continue;
                }

                try
                {
                    // Inside the reconnect try: a transient feeds.json ETag/lock failure must be
                    // retried, not propagated out of ExecuteAsync (which would stop the host).
                    await EnsureSchemas(stoppingToken);
                    await ConnectAndStreamAsync(reconnect, symbols, stoppingToken);
                    // Normal disconnect or deliberate plan-dirty exit — reset before reconnecting.
                    reconnect.Reset();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var decision = reconnect.OnFailure();

                    if (NetworkErrorHelper.IsNetworkError(ex)
                        && decision.Attempt >= options.CurrentValue.NetworkFailureThreshold)
                    {
                        logger.LogError(
                            "SpotAggTradeStreamService — network unreachable after {Count} attempts, tripping circuit breaker",
                            decision.Attempt);
                        circuitBreaker.Trip("Network unreachable (Spot WS)", TripReason.Network);
                        reconnect.Reset();
                        continue;
                    }

                    if (decision.GiveUp)
                    {
                        logger.LogCritical(ex,
                            "SpotAggTradeStreamService exceeded {Max} reconnect attempts, stopping",
                            MaxReconnectAttempts);
                        break;
                    }

                    logger.LogWarning(ex,
                        "SpotAggTradeStreamService disconnected (attempt {Attempt}/{Max}), reconnecting in {Delay}s",
                        decision.Attempt, MaxReconnectAttempts, decision.Delay.TotalSeconds);
                    await Task.Delay(decision.Delay, stoppingToken);
                }
            }
        }
        finally
        {
            planSource.PlanChanged -= onPlanChanged;
        }

        logger.LogInformation("SpotAggTradeStreamService stopped");
    }

    private async Task WaitForPlanChangeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !Volatile.Read(ref _planDirty))
            await Task.Delay(PlanPollInterval, ct);
    }

    private async Task WaitForCircuitResetAsync(CancellationToken ct)
    {
        if (circuitBreaker.IsAutoResettable)
        {
            var probeInterval = options.CurrentValue.NetworkProbeIntervalSeconds;
            logger.LogWarning(
                "SpotAggTradeStreamService paused — network unreachable, probing every {Interval}s",
                probeInterval);

            while (circuitBreaker.IsTripped && circuitBreaker.IsAutoResettable && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(probeInterval), ct);
                if (await ProbeConnectivityAsync(ct))
                {
                    circuitBreaker.Reset();
                    logger.LogInformation("SpotAggTradeStreamService — connectivity restored");
                    return;
                }
            }
        }
        else
        {
            var cooldown = options.CurrentValue.CircuitBreakerCooldownMinutes;
            logger.LogWarning(
                "SpotAggTradeStreamService paused — circuit breaker tripped, retrying in {Cooldown} min",
                cooldown);
            await Task.Delay(TimeSpan.FromMinutes(cooldown), ct);
        }
    }

    private async Task<bool> ProbeConnectivityAsync(CancellationToken ct)
    {
        try
        {
            var baseUrl = options.CurrentValue.Binance.SpotBaseUrl;
            using var client = httpClientFactory.CreateClient("connectivity-probe");
            client.Timeout = TimeSpan.FromSeconds(5);

            using var response = await client.GetAsync($"{baseUrl}/api/v3/ping", ct);
            return true;
        }
        catch (Exception ex) when (
            !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            return false;
        }
    }

    private async Task ConnectAndStreamAsync(
        StreamReconnectPolicy reconnect, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var config = options.CurrentValue;
        var streams = string.Join('/', symbols.Select(s => $"{s.ToLowerInvariant()}@aggTrade"));
        var url = $"{config.Binance.SpotWsBaseUrl}/stream?streams={streams}";

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        reconnect.OnConnected();
        logger.LogInformation(
            "Connected to Binance spot aggTrade combined stream ({Count} symbols)", symbols.Count);

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

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            // Plan changed — exit so the reconnect loop rebuilds subscriptions with updated symbol set.
            if (Volatile.Read(ref _planDirty))
            {
                Volatile.Write(ref _planDirty, false);
                logger.LogInformation("SpotAggTradeStreamService: plan changed, resubscribing");
                break;
            }

            messageStream.SetLength(0);

            WebSocketReceiveResult result;
            do
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(StreamIdleTimeout);
                try
                {
                    result = await ws.ReceiveAsync(buffer, idleCts.Token);
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "SpotAggTrade idle for {Seconds}s (connected but no frames) — reconnecting. " +
                        "The connection may be half-open.",
                        StreamIdleTimeout.TotalSeconds);
                    await FlushStatus(statusTracker, ct);
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogWarning("Spot WS server initiated close: {Status} {Description}",
                        result.CloseStatus, result.CloseStatusDescription);
                    await FlushStatus(statusTracker, ct);
                    return;
                }

                messageStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            try
            {
                totalReceived++;

                var parsed = ParseCombinedAggTrade(
                    new ReadOnlyMemory<byte>(messageStream.GetBuffer(), 0, (int)messageStream.Length));
                if (parsed is null)
                    continue;

                var (symbol, record) = parsed.Value;

                var asset = FindSpotAsset(planSource.Current, symbol);
                if (asset is null)
                    continue;

                var assetDir = BackfillOrchestrator.ResolveAssetDir(
                    options.CurrentValue.DataRoot, asset);
                await schemaManager.EnsureSchema(assetDir, FeedNames.Ticks, "", TickColumns, autoApply: null, ct);
                tickWriter.Write(assetDir, record, asset.DecimalDigits);
                totalWritten++;

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
                logger.LogError(ex, "Failed to process spot aggTrade message");
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
                    "SpotAggTradeStream heartbeat — {Received} events received, {Written} written",
                    totalReceived, totalWritten);
                lastHeartbeat = now;
            }
        }

        await FlushStatus(statusTracker, ct);
    }

    /// <summary>
    /// Parses an aggTrade payload, with or without the combined-stream <c>data</c> wrapper.
    /// </summary>
    internal static (string Symbol, FeedRecord Record)? ParseCombinedAggTrade(ReadOnlyMemory<byte> data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            var payload = root.TryGetProperty("data", out var d) ? d : root;

            if (!payload.TryGetProperty("e", out var eventType)
                || eventType.GetString() != "aggTrade")
                return null;

            if (!payload.TryGetProperty("s", out var symbolProp))
                return null;
            var symbol = symbolProp.GetString();
            if (string.IsNullOrEmpty(symbol))
                return null;

            if (!payload.TryGetProperty("T", out var tsProp))
                return null;
            long timestamp = tsProp.GetInt64();

            if (!payload.TryGetProperty("a", out var aggIdProp))
                return null;
            long aggId = aggIdProp.GetInt64();

            if (!payload.TryGetProperty("p", out var pProp))
                return null;
            if (!double.TryParse(pProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
                return null;

            if (!payload.TryGetProperty("q", out var qProp))
                return null;
            if (!double.TryParse(qProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var qty))
                return null;

            if (!payload.TryGetProperty("m", out var mProp))
                return null;
            bool isBuyerMaker = mProp.GetBoolean();

            return (symbol, new FeedRecord(
                timestamp,
                [price, qty, isBuyerMaker ? 1.0 : 0.0, aggId]));
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
            if (!AssetTypes.IsSpot(asset.Venue.AssetType))
                continue;
            if (!asset.Feeds.Any(f => f.FeedName == FeedNames.Ticks))
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(dataRoot, asset);
            await schemaManager.EnsureSchema(assetDir, FeedNames.Ticks, "", TickColumns, autoApply: null, ct);
        }
    }

    private async Task FlushStatus(Dictionary<string, (long count, long? firstTs, long? lastTs)> tracker, CancellationToken ct)
    {
        foreach (var (assetDir, st) in tracker)
        {
            if (st.count == 0)
                continue;

            await feedStatusStore.Update(assetDir, FeedNames.Ticks, "", existing => new FeedStatus
            {
                FeedName = FeedNames.Ticks,
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

    internal static List<string> BuildEnabledSpotSymbols(CollectionPlan plan) =>
        plan.Assets
            .Where(a => AssetTypes.IsSpot(a.Venue.AssetType))
            .Where(a => a.Feeds.Any(f => f.FeedName == FeedNames.Ticks))
            .Select(a => a.Venue.ApiSymbol)
            .ToList();

    private static CollectionAsset? FindSpotAsset(CollectionPlan plan, string symbol) =>
        plan.Assets.FirstOrDefault(a =>
            AssetTypes.IsSpot(a.Venue.AssetType)
            && string.Equals(a.Venue.ApiSymbol, symbol, StringComparison.OrdinalIgnoreCase));
}
