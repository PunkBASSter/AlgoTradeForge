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
/// to <c>&lt;symbol&gt;@bookTicker</c> for every configured asset that has the
/// <c>book-ticker</c> feed enabled. Spot and futures live on different WS hosts, so the
/// service runs two parallel connections — one against <c>SpotWsBaseUrl</c>, one against
/// <c>FuturesWsBaseUrl</c> — sharing the same writer.
/// </summary>
internal sealed class BookTickerStreamService(
    IBookTickerWriter bookTickerWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ICollectionCircuitBreaker circuitBreaker,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<BookTickerStreamService> logger) : BackgroundService
{
    private static readonly string[] BookTickerColumns =
        ["bid_price", "bid_qty", "ask_price", "ask_qty", "update_id"];
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(5);
    private const int MaxReconnectAttempts = 10;
    private static readonly TimeSpan StatusFlushInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);

    private enum Venue { Spot, Futures }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("BookTickerStreamService started");

        var config = options.CurrentValue;
        var spotSymbols = BuildEnabledSymbols(config, AssetTypes.IsSpot);
        var futuresSymbols = BuildEnabledSymbols(config, AssetTypes.IsFutures);

        if (spotSymbols.Count == 0 && futuresSymbols.Count == 0)
        {
            logger.LogInformation(
                "BookTickerStreamService: no assets with 'book-ticker' feed enabled — exiting");
            return;
        }

        EnsureSchemas(spotSymbols, futuresSymbols);

        var tasks = new List<Task>();
        if (spotSymbols.Count > 0)
            tasks.Add(VenueLoopAsync(Venue.Spot, spotSymbols, stoppingToken));
        if (futuresSymbols.Count > 0)
            tasks.Add(VenueLoopAsync(Venue.Futures, futuresSymbols, stoppingToken));

        await Task.WhenAll(tasks);

        logger.LogInformation("BookTickerStreamService stopped");
    }

    private async Task VenueLoopAsync(Venue venue, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        int attempts = 0;

        while (!ct.IsCancellationRequested)
        {
            if (circuitBreaker.IsTripped)
            {
                await WaitForCircuitResetAsync(venue, ct);
                continue;
            }

            try
            {
                await ConnectAndStreamAsync(venue, symbols, ct);
                attempts = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
                        "BookTickerStreamService[{Venue}] — network unreachable after {Count} attempts, tripping circuit breaker",
                        venue, attempts);
                    circuitBreaker.Trip($"Network unreachable (BookTicker {venue})", TripReason.Network);
                    attempts = 0;
                    continue;
                }

                if (attempts > MaxReconnectAttempts)
                {
                    logger.LogCritical(ex,
                        "BookTickerStreamService[{Venue}] exceeded {Max} reconnect attempts, stopping",
                        venue, MaxReconnectAttempts);
                    break;
                }

                var delay = InitialReconnectDelay.TotalSeconds * Math.Pow(2, attempts - 1);
                logger.LogWarning(ex,
                    "BookTickerStreamService[{Venue}] disconnected (attempt {Attempt}/{Max}), reconnecting in {Delay}s",
                    venue, attempts, MaxReconnectAttempts, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
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

    private async Task ConnectAndStreamAsync(Venue venue, IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var config = options.CurrentValue;
        var wsBase = venue == Venue.Spot
            ? config.Binance.SpotWsBaseUrl
            : config.Binance.FuturesWsBaseUrl;
        var streams = string.Join('/', symbols.Select(s => $"{s.ToLowerInvariant()}@bookTicker"));
        var url = $"{wsBase}/stream?streams={streams}";

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        logger.LogInformation(
            "BookTickerStreamService[{Venue}] connected ({Count} symbols)", venue, symbols.Count);

        await ReadLoopAsync(venue, ws, config, ct);
    }

    private async Task ReadLoopAsync(Venue venue, ClientWebSocket ws, HistoryLoaderOptions config, CancellationToken ct)
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
                    logger.LogWarning(
                        "BookTicker[{Venue}] WS server-initiated close: {Status} {Description}",
                        venue, result.CloseStatus, result.CloseStatusDescription);
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

                var asset = FindAssetConfig(config, venue, symbol);
                if (asset is null)
                    continue;

                var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
                schemaManager.EnsureSchema(assetDir, FeedNames.BookTicker, "", BookTickerColumns);
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

    private void EnsureSchemas(IReadOnlyList<string> spotSymbols, IReadOnlyList<string> futuresSymbols)
    {
        var config = options.CurrentValue;
        var spotSet = spotSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var futuresSet = futuresSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in config.Assets)
        {
            bool included = (AssetTypes.IsSpot(asset.Type) && spotSet.Contains(asset.Symbol))
                          || (AssetTypes.IsFutures(asset.Type) && futuresSet.Contains(asset.Symbol));
            if (!included)
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
            schemaManager.EnsureSchema(assetDir, FeedNames.BookTicker, "", BookTickerColumns);
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

    private static List<string> BuildEnabledSymbols(HistoryLoaderOptions config, Func<string, bool> typeFilter) =>
        config.Assets
            .Where(a => typeFilter(a.Type))
            .Where(a => a.Feeds.Any(f => f.Enabled && f.Name == FeedNames.BookTicker))
            .Select(a => a.Symbol)
            .ToList();

    private static AssetCollectionConfig? FindAssetConfig(HistoryLoaderOptions config, Venue venue, string symbol) =>
        config.Assets.FirstOrDefault(a =>
            (venue == Venue.Spot ? AssetTypes.IsSpot(a.Type) : AssetTypes.IsFutures(a.Type))
            && string.Equals(a.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
}
