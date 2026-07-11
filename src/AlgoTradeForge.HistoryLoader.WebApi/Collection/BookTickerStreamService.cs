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
/// Real-time best-bid/ask collection via Binance WebSocket combined streams. Subscribes
/// to <c>&lt;symbol&gt;@bookTicker</c> for every plan asset that has the <c>book-ticker</c>
/// feed collected eagerly. Spot and futures live on different WS hosts, so the service
/// runs two parallel connections — one against <c>SpotWsBaseUrl</c>, one against
/// <c>FuturesWsBaseUrl</c> — sharing the same writer.
/// </summary>
internal sealed class BookTickerStreamService(
    IBookTickerWriter bookTickerWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ICollectionCircuitBreaker circuitBreaker,
    IHttpClientFactory httpClientFactory,
    ICollectionPlanSource planSource,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<BookTickerStreamService> logger) : BackgroundService
{
    private static readonly string[] BookTickerColumns =
        ["bid_price", "bid_qty", "ask_price", "ask_qty", "update_id"];
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(5);
    private const int MaxReconnectAttempts = 10;
    private static readonly TimeSpan StableConnectionUptime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StatusFlushInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PlanPollInterval = TimeSpan.FromSeconds(1);

    private enum Venue { Spot, Futures }

    // Per-venue dirty flags: both venue loops run concurrently and each must observe
    // every plan change independently — a single shared flag would be consumed by
    // whichever loop wakes first, leaving the other on stale subscriptions.
    private bool _spotPlanDirty;
    private bool _futuresPlanDirty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("BookTickerStreamService started");

        // Subscribe BEFORE the first plan read — at boot Current is CollectionPlan.Empty
        // until DesiredStateService publishes; an empty read must not exit the service.
        Action onPlanChanged = () =>
        {
            Volatile.Write(ref _spotPlanDirty, true);
            Volatile.Write(ref _futuresPlanDirty, true);
        };
        planSource.PlanChanged += onPlanChanged;
        try
        {
            await Task.WhenAll(
                VenueLoopAsync(Venue.Spot, stoppingToken),
                VenueLoopAsync(Venue.Futures, stoppingToken));
        }
        finally
        {
            planSource.PlanChanged -= onPlanChanged;
        }

        logger.LogInformation("BookTickerStreamService stopped");
    }

    private bool ConsumeDirty(Venue venue)
    {
        if (venue == Venue.Spot)
        {
            if (!Volatile.Read(ref _spotPlanDirty))
                return false;
            Volatile.Write(ref _spotPlanDirty, false);
            return true;
        }

        if (!Volatile.Read(ref _futuresPlanDirty))
            return false;
        Volatile.Write(ref _futuresPlanDirty, false);
        return true;
    }

    private bool IsDirty(Venue venue) => venue == Venue.Spot
        ? Volatile.Read(ref _spotPlanDirty)
        : Volatile.Read(ref _futuresPlanDirty);

    private async Task WaitForPlanChangeAsync(Venue venue, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !IsDirty(venue))
            await Task.Delay(PlanPollInterval, ct);
    }

    private async Task VenueLoopAsync(Venue venue, CancellationToken ct)
    {
        var reconnect = new StreamReconnectPolicy(
            MaxReconnectAttempts, InitialReconnectDelay, StableConnectionUptime);
        var typeFilter = venue == Venue.Spot
            ? (Func<string, bool>)AssetTypes.IsSpot
            : AssetTypes.IsFutures;

        while (!ct.IsCancellationRequested)
        {
            if (circuitBreaker.IsTripped)
            {
                await WaitForCircuitResetAsync(venue, ct);
                continue;
            }

            // Clear-then-read: a PlanChanged after this clear re-marks dirty and is seen
            // by the read loop; one before it is captured by the fresh Current read.
            ConsumeDirty(venue);
            var symbols = BuildEnabledSymbols(planSource.Current, typeFilter);

            if (symbols.Count == 0)
            {
                // No eligible symbols (plan may still be Empty at boot) — wait, don't exit.
                await WaitForPlanChangeAsync(venue, ct);
                continue;
            }

            await EnsureSchemas(typeFilter, ct);

            try
            {
                await ConnectAndStreamAsync(reconnect, venue, symbols, ct);
                // Normal disconnect or deliberate plan-dirty exit — reset before reconnecting.
                reconnect.Reset();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
                        "BookTickerStreamService[{Venue}] — network unreachable after {Count} attempts, tripping circuit breaker",
                        venue, decision.Attempt);
                    circuitBreaker.Trip($"Network unreachable (BookTicker {venue})", TripReason.Network);
                    reconnect.Reset();
                    continue;
                }

                if (decision.GiveUp)
                {
                    logger.LogCritical(ex,
                        "BookTickerStreamService[{Venue}] exceeded {Max} reconnect attempts, stopping",
                        venue, MaxReconnectAttempts);
                    break;
                }

                logger.LogWarning(ex,
                    "BookTickerStreamService[{Venue}] disconnected (attempt {Attempt}/{Max}), reconnecting in {Delay}s",
                    venue, decision.Attempt, MaxReconnectAttempts, decision.Delay.TotalSeconds);
                await Task.Delay(decision.Delay, ct);
            }
        }
    }

    private async Task WaitForCircuitResetAsync(Venue venue, CancellationToken ct)
    {
        if (circuitBreaker.IsAutoResettable)
        {
            var probeInterval = options.CurrentValue.NetworkProbeIntervalSeconds;
            while (circuitBreaker.IsTripped && circuitBreaker.IsAutoResettable && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(probeInterval), ct);
                if (await ProbeConnectivityAsync(venue, ct))
                {
                    circuitBreaker.Reset();
                    logger.LogInformation("BookTickerStreamService[{Venue}] connectivity restored", venue);
                    return;
                }
            }
        }
        else
        {
            var cooldown = options.CurrentValue.CircuitBreakerCooldownMinutes;
            await Task.Delay(TimeSpan.FromMinutes(cooldown), ct);
        }
    }

    private async Task<bool> ProbeConnectivityAsync(Venue venue, CancellationToken ct)
    {
        try
        {
            var binance = options.CurrentValue.Binance;
            var pingUrl = venue == Venue.Spot
                ? $"{binance.SpotBaseUrl}/api/v3/ping"
                : $"{binance.FuturesBaseUrl}/fapi/v1/ping";
            using var client = httpClientFactory.CreateClient("connectivity-probe");
            client.Timeout = TimeSpan.FromSeconds(5);
            using var response = await client.GetAsync(pingUrl, ct);
            return true;
        }
        catch (Exception ex) when (
            !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            return false;
        }
    }

    private async Task ConnectAndStreamAsync(
        StreamReconnectPolicy reconnect, Venue venue, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var config = options.CurrentValue;
        var wsBase = venue == Venue.Spot
            ? config.Binance.SpotWsBaseUrl
            : config.Binance.FuturesWsBaseUrl;
        var streams = string.Join('/', symbols.Select(s => $"{s.ToLowerInvariant()}@bookTicker"));
        var url = $"{wsBase}/stream?streams={streams}";

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        reconnect.OnConnected();
        logger.LogInformation(
            "BookTickerStreamService[{Venue}] connected ({Count} symbols)", venue, symbols.Count);

        await ReadLoopAsync(venue, ws, ct);
    }

    private async Task ReadLoopAsync(Venue venue, ClientWebSocket ws, CancellationToken ct)
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
            // Plan changed — exit so VenueLoopAsync rebuilds subscriptions with updated symbol set.
            if (ConsumeDirty(venue))
            {
                logger.LogInformation(
                    "BookTickerStreamService[{Venue}]: plan changed, resubscribing", venue);
                break;
            }

            messageStream.SetLength(0);

            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogWarning(
                        "BookTicker[{Venue}] WS server-initiated close: {Status} {Description}",
                        venue, result.CloseStatus, result.CloseStatusDescription);
                    await FlushStatus(statusTracker, ct);
                    return;
                }
                messageStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            try
            {
                totalReceived++;

                var parsed = ParseBookTicker(
                    new ReadOnlyMemory<byte>(messageStream.GetBuffer(), 0, (int)messageStream.Length));
                if (parsed is null)
                    continue;

                var (symbol, record) = parsed.Value;

                var asset = FindAsset(planSource.Current, venue, symbol);
                if (asset is null)
                    continue;

                var assetDir = BackfillOrchestrator.ResolveAssetDir(
                    options.CurrentValue.DataRoot, asset);
                await schemaManager.EnsureSchema(assetDir, FeedNames.BookTicker, "", BookTickerColumns, ct: ct);
                bookTickerWriter.Write(assetDir, record);
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
                logger.LogError(ex, "BookTicker[{Venue}] failed to process message", venue);
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
                    "BookTicker[{Venue}] heartbeat — {Received} events received, {Written} written",
                    venue, totalReceived, totalWritten);
                lastHeartbeat = now;
            }
        }

        await FlushStatus(statusTracker, ct);
    }

    /// <summary>
    /// Parses a Binance book-ticker payload, with or without the combined-stream wrapper.
    /// Spot and futures share the same field names: <c>u, s, b, B, a, A</c>; futures and
    /// recent spot also include <c>T</c> (transaction time) and <c>E</c> (event time).
    /// Falls back to current UTC time if neither timestamp field is present (older spot).
    /// </summary>
    internal static (string Symbol, FeedRecord Record)? ParseBookTicker(ReadOnlyMemory<byte> data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            var payload = root.TryGetProperty("data", out var d) ? d : root;

            if (!payload.TryGetProperty("s", out var symbolProp))
                return null;
            var symbol = symbolProp.GetString();
            if (string.IsNullOrEmpty(symbol))
                return null;

            if (!payload.TryGetProperty("u", out var uProp))
                return null;
            long updateId = uProp.GetInt64();

            // Prefer transaction time, fall back to event time, fall back to wall clock.
            long timestamp;
            if (payload.TryGetProperty("T", out var tProp))
                timestamp = tProp.GetInt64();
            else if (payload.TryGetProperty("E", out var eProp))
                timestamp = eProp.GetInt64();
            else
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!payload.TryGetProperty("b", out var bProp)) return null;
            if (!double.TryParse(bProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bidPrice))
                return null;

            if (!payload.TryGetProperty("B", out var bQtyProp)) return null;
            if (!double.TryParse(bQtyProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bidQty))
                return null;

            if (!payload.TryGetProperty("a", out var aProp)) return null;
            if (!double.TryParse(aProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var askPrice))
                return null;

            if (!payload.TryGetProperty("A", out var aQtyProp)) return null;
            if (!double.TryParse(aQtyProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var askQty))
                return null;

            return (symbol, new FeedRecord(
                timestamp,
                [bidPrice, bidQty, askPrice, askQty, updateId]));
        }
        catch
        {
            return null;
        }
    }

    private async Task EnsureSchemas(Func<string, bool> typeFilter, CancellationToken ct)
    {
        var dataRoot = options.CurrentValue.DataRoot;
        foreach (var asset in planSource.Current.Assets)
        {
            if (!typeFilter(asset.Venue.AssetType))
                continue;
            if (!asset.Feeds.Any(f => f.FeedName == FeedNames.BookTicker && f.Collect == "eager"))
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(dataRoot, asset);
            await schemaManager.EnsureSchema(assetDir, FeedNames.BookTicker, "", BookTickerColumns, ct: ct);
        }
    }

    private async Task FlushStatus(Dictionary<string, (long count, long? firstTs, long? lastTs)> tracker, CancellationToken ct)
    {
        foreach (var (assetDir, st) in tracker)
        {
            if (st.count == 0) continue;

            var existing = await feedStatusStore.Load(assetDir, FeedNames.BookTicker, "", ct);
            await feedStatusStore.Save(assetDir, FeedNames.BookTicker, "", new FeedStatus
            {
                FeedName = FeedNames.BookTicker,
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

    internal static List<string> BuildEnabledSymbols(
        CollectionPlan plan, Func<string, bool> typeFilter) =>
        plan.Assets
            .Where(a => typeFilter(a.Venue.AssetType))
            .Where(a => a.Feeds.Any(f => f.FeedName == FeedNames.BookTicker && f.Collect == "eager"))
            .Select(a => a.Venue.ApiSymbol)
            .ToList();

    private static CollectionAsset? FindAsset(CollectionPlan plan, Venue venue, string symbol) =>
        plan.Assets.FirstOrDefault(a =>
            (venue == Venue.Spot ? AssetTypes.IsSpot(a.Venue.AssetType) : AssetTypes.IsFutures(a.Venue.AssetType))
            && string.Equals(a.Venue.ApiSymbol, symbol, StringComparison.OrdinalIgnoreCase));
}
