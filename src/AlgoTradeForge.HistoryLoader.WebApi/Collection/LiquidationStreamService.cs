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
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<LiquidationStreamService> logger) : BackgroundService
{
    private const string StreamPath = "/ws/!forceOrder@arr";
    private static readonly string[] Columns = ["side", "price", "qty", "notional_usd"];
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(5);
    private const int MaxReconnectAttempts = 10;
    private static readonly TimeSpan StatusFlushInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("LiquidationStreamService started");

        EnsureSchemas();

        int attempts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (circuitBreaker.IsTripped)
            {
                var cooldown = options.CurrentValue.CircuitBreakerCooldownMinutes;
                logger.LogWarning(
                    "LiquidationStreamService paused — circuit breaker tripped, retrying in {Cooldown} min",
                    cooldown);
                await Task.Delay(TimeSpan.FromMinutes(cooldown), stoppingToken);
                continue;
            }

            try
            {
                await ConnectAndStreamAsync(stoppingToken);
                // Normal disconnect (e.g. server-side close) — reset and reconnect
                attempts = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                attempts++;
                if (attempts > MaxReconnectAttempts)
                {
                    logger.LogCritical(ex,
                        "LiquidationStreamService exceeded {Max} reconnect attempts, stopping",
                        MaxReconnectAttempts);
                    break;
                }

                var delay = InitialReconnectDelay.TotalSeconds * Math.Pow(2, attempts - 1);
                logger.LogWarning(ex,
                    "LiquidationStreamService disconnected (attempt {Attempt}/{Max}), reconnecting in {Delay}s",
                    attempts, MaxReconnectAttempts, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
            }
        }

        logger.LogInformation("LiquidationStreamService stopped");
    }

    private async Task ConnectAndStreamAsync(CancellationToken ct)
    {
        var config = options.CurrentValue;
        var url = $"{config.Binance.FuturesWsBaseUrl}{StreamPath}";

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        logger.LogInformation("Connected to !forceOrder@arr stream at {Url}", url);

        var enabledSymbols = BuildEnabledSymbolSet(config);
        await ReadLoopAsync(ws, enabledSymbols, config, ct);
    }

    private async Task ReadLoopAsync(
        ClientWebSocket ws,
        HashSet<string> enabledSymbols,
        HistoryLoaderOptions config,
        CancellationToken ct)
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

                var asset = FindAssetConfig(config, symbol);
                if (asset is null)
                    continue;

                var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
                schemaManager.EnsureSchema(assetDir, FeedNames.Liquidations, "", Columns);
                feedWriter.Write(assetDir, FeedNames.Liquidations, "", Columns, record);
                totalWritten++;

                var sideLabel = record.Values[0] > 0 ? "LONG" : "SHORT";
                logger.LogDebug(
                    "Liquidation {Symbol} {Side} qty={Qty} price={Price} notional=${Notional:F2}",
                    symbol, sideLabel, record.Values[2], record.Values[1], record.Values[3]);

                // Track status
                if (!statusTracker.TryGetValue(assetDir, out var st))
                    st = (0, null, null);
                st.count++;
                st.firstTs ??= record.TimestampMs;
                st.lastTs = record.TimestampMs;
                statusTracker[assetDir] = st;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to process liquidation message");
            }

            var now = DateTimeOffset.UtcNow;

            // Periodic status flush
            if (now - lastStatusFlush >= StatusFlushInterval)
            {
                FlushStatus(statusTracker);
                lastStatusFlush = now;
            }

            // Periodic heartbeat
            if (now - lastHeartbeat >= HeartbeatInterval)
            {
                logger.LogInformation(
                    "LiquidationStream heartbeat — {Received} events received, {Written} written for tracked symbols",
                    totalReceived, totalWritten);
                lastHeartbeat = now;
            }
        }

        // Final flush
        FlushStatus(statusTracker);
    }

    internal static (string Symbol, FeedRecord Record)? ParseForceOrder(ReadOnlyMemory<byte> data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            // Verify event type
            if (!root.TryGetProperty("e", out var eventType)
                || eventType.GetString() != "forceOrder")
                return null;

            if (!root.TryGetProperty("o", out var order))
                return null;

            // Only process FILLED orders
            if (!order.TryGetProperty("X", out var status)
                || status.GetString() != "FILLED")
                return null;

            // Extract symbol
            if (!order.TryGetProperty("s", out var symbolProp))
                return null;
            var symbol = symbolProp.GetString();
            if (string.IsNullOrEmpty(symbol))
                return null;

            // Extract timestamp
            if (!order.TryGetProperty("T", out var tsProp))
                return null;
            long timestamp = tsProp.ValueKind == JsonValueKind.Number
                ? tsProp.GetInt64()
                : long.Parse(tsProp.GetString()!, CultureInfo.InvariantCulture);

            // Extract side: SELL = long liquidated (1.0), BUY = short liquidated (-1.0)
            if (!order.TryGetProperty("S", out var sideProp))
                return null;
            var sideStr = sideProp.GetString();
            double side = sideStr == "SELL" ? 1.0 : sideStr == "BUY" ? -1.0 : double.NaN;
            if (double.IsNaN(side))
                return null;

            // Extract average price
            if (!order.TryGetProperty("ap", out var apProp))
                return null;
            var apStr = apProp.GetString();
            if (!double.TryParse(apStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var avgPrice))
                return null;

            // Extract executed qty
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

    private void EnsureSchemas()
    {
        var config = options.CurrentValue;

        foreach (var asset in config.Assets)
        {
            if (!AssetTypes.IsFutures(asset.Type))
                continue;

            var hasLiquidation = asset.Feeds.Any(f => f.Enabled && f.Name == FeedNames.Liquidations);
            if (!hasLiquidation)
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
            schemaManager.EnsureSchema(assetDir, FeedNames.Liquidations, "", Columns);
        }
    }

    private void FlushStatus(Dictionary<string, (long count, long? firstTs, long? lastTs)> tracker)
    {
        foreach (var (assetDir, st) in tracker)
        {
            if (st.count == 0)
                continue;

            var existing = feedStatusStore.Load(assetDir, FeedNames.Liquidations, "");

            feedStatusStore.Save(assetDir, FeedNames.Liquidations, "", new FeedStatus
            {
                FeedName = FeedNames.Liquidations,
                Interval = "",
                FirstTimestamp = existing?.FirstTimestamp ?? st.firstTs,
                LastTimestamp = st.lastTs,
                LastRunUtc = DateTimeOffset.UtcNow,
                RecordCount = (existing?.RecordCount ?? 0) + st.count,
                Health = CollectionHealth.Healthy
            });
        }

        tracker.Clear();
    }

    private static HashSet<string> BuildEnabledSymbolSet(HistoryLoaderOptions config) =>
        config.Assets
            .Where(a => AssetTypes.IsFutures(a.Type))
            .Where(a => a.Feeds.Any(f => f.Enabled && f.Name == FeedNames.Liquidations))
            .Select(a => a.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static AssetCollectionConfig? FindAssetConfig(HistoryLoaderOptions config, string symbol) =>
        config.Assets.FirstOrDefault(a =>
            AssetTypes.IsFutures(a.Type)
            && string.Equals(a.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
}
