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
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<SpotAggTradeStreamService> logger) : BackgroundService
{
    private static readonly string[] TickColumns = ["price", "qty", "is_buyer_maker", "agg_id"];
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(5);
    private const int MaxReconnectAttempts = 10;
    private static readonly TimeSpan StatusFlushInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SpotAggTradeStreamService started");

        var enabledSpotSymbols = BuildEnabledSpotSymbols(options.CurrentValue);
        if (enabledSpotSymbols.Count == 0)
        {
            logger.LogInformation(
                "SpotAggTradeStreamService: no spot assets with 'ticks' feed enabled — exiting");
            return;
        }

        EnsureSchemas(enabledSpotSymbols);

        int attempts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (circuitBreaker.IsTripped)
            {
                await WaitForCircuitResetAsync(stoppingToken);
                continue;
            }

            try
            {
                await ConnectAndStreamAsync(enabledSpotSymbols, stoppingToken);
                attempts = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempts++;

                if (NetworkErrorHelper.IsNetworkError(ex)
                    && attempts >= options.CurrentValue.NetworkFailureThreshold)
                {
                    logger.LogError(
                        "SpotAggTradeStreamService — network unreachable after {Count} attempts, tripping circuit breaker",
                        attempts);
                    circuitBreaker.Trip("Network unreachable (Spot WS)", TripReason.Network);
                    attempts = 0;
                    continue;
                }

                if (attempts > MaxReconnectAttempts)
                {
                    logger.LogCritical(ex,
                        "SpotAggTradeStreamService exceeded {Max} reconnect attempts, stopping",
                        MaxReconnectAttempts);
                    break;
                }

                var delay = InitialReconnectDelay.TotalSeconds * Math.Pow(2, attempts - 1);
                logger.LogWarning(ex,
                    "SpotAggTradeStreamService disconnected (attempt {Attempt}/{Max}), reconnecting in {Delay}s",
                    attempts, MaxReconnectAttempts, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
            }
        }

        logger.LogInformation("SpotAggTradeStreamService stopped");
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

    private async Task ConnectAndStreamAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var config = options.CurrentValue;
        var streams = string.Join('/', symbols.Select(s => $"{s.ToLowerInvariant()}@aggTrade"));
        var url = $"{config.Binance.SpotWsBaseUrl}/stream?streams={streams}";

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        logger.LogInformation(
            "Connected to Binance spot aggTrade combined stream ({Count} symbols)", symbols.Count);

        await ReadLoopAsync(ws, config, ct);
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, HistoryLoaderOptions config, CancellationToken ct)
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
            messageStream.SetLength(0);

            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogWarning("Spot WS server initiated close: {Status} {Description}",
                        result.CloseStatus, result.CloseStatusDescription);
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

                var asset = FindSpotAssetConfig(config, symbol);
                if (asset is null)
                    continue;

                var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
                schemaManager.EnsureSchema(assetDir, FeedNames.Ticks, "", TickColumns, autoApply: null);
                tickWriter.Write(assetDir, record);
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

    private void EnsureSchemas(IReadOnlyList<string> symbols)
    {
        var config = options.CurrentValue;
        var symbolSet = symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in config.Assets)
        {
            if (!AssetTypes.IsSpot(asset.Type))
                continue;
            if (!symbolSet.Contains(asset.Symbol))
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
            schemaManager.EnsureSchema(assetDir, FeedNames.Ticks, "", TickColumns, autoApply: null);
        }
    }

    private async Task FlushStatus(Dictionary<string, (long count, long? firstTs, long? lastTs)> tracker, CancellationToken ct)
    {
        foreach (var (assetDir, st) in tracker)
        {
            if (st.count == 0)
                continue;

            var existing = await feedStatusStore.Load(assetDir, FeedNames.Ticks, "", ct);

            await feedStatusStore.Save(assetDir, FeedNames.Ticks, "", new FeedStatus
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

    private static List<string> BuildEnabledSpotSymbols(HistoryLoaderOptions config) =>
        config.Assets
            .Where(a => AssetTypes.IsSpot(a.Type))
            .Where(a => a.Feeds.Any(f => f.Enabled && f.Name == FeedNames.Ticks))
            .Select(a => a.Symbol)
            .ToList();

    private static AssetCollectionConfig? FindSpotAssetConfig(HistoryLoaderOptions config, string symbol) =>
        config.Assets.FirstOrDefault(a =>
            AssetTypes.IsSpot(a.Type)
            && string.Equals(a.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
}
