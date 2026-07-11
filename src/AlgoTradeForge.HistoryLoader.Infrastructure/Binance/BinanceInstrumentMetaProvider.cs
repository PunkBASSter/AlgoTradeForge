using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

/// <summary>Fetches Binance exchangeInfo (one spot + one futures call — each returns EVERY
/// symbol) and upserts instrument_meta. Decimals derive from PRICE_FILTER.tickSize and
/// LOT_SIZE.stepSize — NEVER pricePrecision (futures pricePrecision is the API field width,
/// spot has no precision fields). Dir mapping via SymbologyRegistry conventions: futures
/// symbols get the "_perp" dir suffix (AssetDirectoryName convention), spot symbols the bare
/// API symbol. In-memory last-fetch per venue class enforces the 24h TTL.</summary>
public sealed class BinanceInstrumentMetaProvider(
    IHttpClientFactory httpClientFactory,
    IHistoryIndex index,
    IOptionsMonitor<HistoryLoaderOptions> options,
    TimeProvider clock,
    ILogger<BinanceInstrumentMetaProvider> logger) : IInstrumentMetaProvider
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;

    public async Task EnsureFresh(string exchange, CancellationToken ct = default)
    {
        if (!string.Equals(exchange, "binance", StringComparison.OrdinalIgnoreCase)) return;
        if (clock.GetUtcNow() - _lastFetch < Ttl) return;
        using var _ = await _gate.LockAsync(ct);
        if (clock.GetUtcNow() - _lastFetch < Ttl) return;

        var binance = options.CurrentValue.Binance;
        var fetchedAt = clock.GetUtcNow().UtcDateTime.ToString("O");
        // Upsert per venue class immediately: if the second fetch throws, the first class's rows
        // are already persisted (partial success shrinks the blocked set under degradation).
        await index.UpsertInstrumentMeta(
            await Fetch($"{binance.FuturesBaseUrl}/fapi/v1/exchangeInfo", isFutures: true, fetchedAt, ct), ct);
        await index.UpsertInstrumentMeta(
            await Fetch($"{binance.SpotBaseUrl}/api/v3/exchangeInfo", isFutures: false, fetchedAt, ct), ct);
        _lastFetch = clock.GetUtcNow();
        logger.LogInformation("instrument_meta refreshed");
    }

    private async Task<List<InstrumentMetaRow>> Fetch(string url, bool isFutures, string fetchedAt, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("binance-meta");
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var rows = new List<InstrumentMetaRow>();
        foreach (var sym in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            var apiSymbol = sym.GetProperty("symbol").GetString()!;
            string? tickSize = null, stepSize = null;
            foreach (var filter in sym.GetProperty("filters").EnumerateArray())
            {
                var type = filter.GetProperty("filterType").GetString();
                if (type == "PRICE_FILTER") tickSize = filter.GetProperty("tickSize").GetString();
                else if (type == "LOT_SIZE") stepSize = filter.GetProperty("stepSize").GetString();
            }
            if (tickSize is null) continue;
            // dir derivation duplicates AssetDirectoryName/BinanceSymbology convention on purpose —
            // meta covers ALL venue symbols, most of which no group declares.
            var dir = isFutures ? $"{apiSymbol}_perp" : apiSymbol;
            rows.Add(new InstrumentMetaRow("binance", dir,
                TickSizeParser.FractionalDigits(tickSize),
                stepSize is null ? 0 : TickSizeParser.FractionalDigits(stepSize),
                tickSize, fetchedAt));
        }
        return rows;
    }
}
