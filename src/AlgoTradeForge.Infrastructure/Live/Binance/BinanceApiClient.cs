using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoTradeForge.Application.Live;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.Live.Binance;

public sealed class BinanceApiClient : IExchangeOrderClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly byte[] _secretBytes;
    private readonly ILogger _logger;
    private long _serverTimeOffsetMs;

    public BinanceApiClient(string baseRestUrl, string apiKey, string apiSecret, ILogger logger)
    {
        _logger = logger;
        _secretBytes = Encoding.UTF8.GetBytes(apiSecret);
        _http = new HttpClient { BaseAddress = new Uri(baseRestUrl) };
        _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", apiKey);
    }

    /// <summary>
    /// Syncs the local clock with Binance server time to avoid timestamp errors.
    /// Must be called before any signed request.
    /// </summary>
    public async Task SyncTimeAsync(CancellationToken ct = default)
    {
        var localBefore = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v3/time");
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var localAfter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to sync Binance server time: {StatusCode} {Body}", response.StatusCode, body);
            return;
        }

        using var doc = JsonDocument.Parse(body);
        var serverTime = doc.RootElement.GetProperty("serverTime").GetInt64();
        var localMid = (localBefore + localAfter) / 2;
        _serverTimeOffsetMs = serverTime - localMid;

        _logger.LogInformation("Binance time sync: offset={OffsetMs}ms", _serverTimeOffsetMs);
    }

    internal long GetTimestamp() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _serverTimeOffsetMs;

    public async Task<BinanceNewOrderResponse> PlaceOrderAsync(
        string symbol, string side, string type, decimal quantity,
        decimal? price = null, decimal? stopPrice = null,
        CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["side"] = side.ToUpperInvariant(),
            ["type"] = type.ToUpperInvariant(),
            ["quantity"] = quantity.ToString("G29"),
            ["newOrderRespType"] = "FULL",
        };

        if (type.Equals("LIMIT", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("STOP_LOSS_LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            parameters["timeInForce"] = "GTC";
        }

        if (price.HasValue)
            parameters["price"] = price.Value.ToString("G29");

        if (stopPrice.HasValue)
            parameters["stopPrice"] = stopPrice.Value.ToString("G29");

        var json = await SendSignedAsync(HttpMethod.Post, "/api/v3/order", parameters, ct);
        return JsonSerializer.Deserialize<BinanceNewOrderResponse>(json, BinanceJsonOptions.Default)!;
    }

    async Task<ExchangeOrderResult> IExchangeOrderClient.PlaceOrderAsync(
        string symbol, string side, string type, decimal quantity,
        decimal? price, decimal? stopPrice, CancellationToken ct)
    {
        var response = await PlaceOrderAsync(symbol, side, type, quantity, price, stopPrice, ct);
        var fills = response.Fills
            .Select(f => new ExchangeFill(
                decimal.Parse(f.Price, CultureInfo.InvariantCulture),
                decimal.Parse(f.Qty, CultureInfo.InvariantCulture),
                decimal.Parse(f.Commission, CultureInfo.InvariantCulture)))
            .ToList();
        return new ExchangeOrderResult(response.OrderId, fills);
    }

    public async Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["orderId"] = orderId.ToString(),
        };
        await SendSignedAsync(HttpMethod.Delete, "/api/v3/order", parameters, ct);
    }

    public async Task<BinanceAccountInfo> GetAccountInfoAsync(CancellationToken ct = default)
    {
        var json = await SendSignedAsync(HttpMethod.Get, "/api/v3/account", new Dictionary<string, string>(), ct);
        return JsonSerializer.Deserialize<BinanceAccountInfo>(json, BinanceJsonOptions.Default)!;
    }

    public async Task<BinanceExchangeSymbolInfo> GetExchangeInfoAsync(string symbol, CancellationToken ct = default)
    {
        var url = $"/api/v3/exchangeInfo?symbol={symbol.ToUpperInvariant()}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Binance exchangeInfo error: {StatusCode} {Body}", response.StatusCode, body);
            throw new HttpRequestException($"Binance exchangeInfo error {response.StatusCode}: {body}");
        }

        var info = JsonSerializer.Deserialize<BinanceExchangeInfoResponse>(body, BinanceJsonOptions.Default)!;
        return info.Symbols.FirstOrDefault(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Symbol '{symbol}' not found in Binance exchange info.");
    }

    internal async Task<decimal> GetTickerPriceAsync(string symbol, CancellationToken ct = default)
    {
        var url = $"/api/v3/ticker/price?symbol={symbol.ToUpperInvariant()}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Binance ticker/price error: {StatusCode} {Body}", response.StatusCode, body);
            throw new HttpRequestException($"Binance ticker/price error {response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        return decimal.Parse(doc.RootElement.GetProperty("price").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Fetch klines (candlestick bars) from Binance REST API.
    /// Returns raw arrays: [openTime, open, high, low, close, volume, ...].
    /// </summary>
    internal async Task<List<Int64Bar>> GetKlinesAsync(
        string symbol, string interval, decimal tickSize, int limit = 500, CancellationToken ct = default)
    {
        var url = $"/api/v3/klines?symbol={symbol.ToUpperInvariant()}&interval={interval}&limit={limit}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Binance klines error: {StatusCode} {Body}", response.StatusCode, body);
            throw new HttpRequestException($"Binance klines error {response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var scale = new ScaleContext(tickSize);
        var bars = new List<Int64Bar>();
        foreach (var kline in doc.RootElement.EnumerateArray())
        {
            var openTime = kline[0].GetInt64();
            var open = decimal.Parse(kline[1].GetString()!, CultureInfo.InvariantCulture);
            var high = decimal.Parse(kline[2].GetString()!, CultureInfo.InvariantCulture);
            var low = decimal.Parse(kline[3].GetString()!, CultureInfo.InvariantCulture);
            var close = decimal.Parse(kline[4].GetString()!, CultureInfo.InvariantCulture);
            var volume = decimal.Parse(kline[5].GetString()!, CultureInfo.InvariantCulture);

            bars.Add(new Int64Bar(
                openTime,
                scale.FromMarketPrice(open),
                scale.FromMarketPrice(high),
                scale.FromMarketPrice(low),
                scale.FromMarketPrice(close),
                MoneyConvert.ToLong(volume))); // Volume: not monetary, rounding is correct for fractional quantities
        }

        return bars;
    }

    public async Task<IReadOnlyList<BinanceMyTrade>> GetMyTradesAsync(
        string symbol, int limit = 50, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["limit"] = limit.ToString(),
        };
        var json = await SendSignedAsync(HttpMethod.Get, "/api/v3/myTrades", parameters, ct);
        return JsonSerializer.Deserialize<List<BinanceMyTrade>>(json, BinanceJsonOptions.Default) ?? [];
    }

    internal string Sign(string queryString)
    {
        var hash = HMACSHA256.HashData(_secretBytes, Encoding.UTF8.GetBytes(queryString));
        return Convert.ToHexStringLower(hash);
    }

    private async Task<string> SendSignedAsync(
        HttpMethod method, string endpoint, Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        parameters["timestamp"] = GetTimestamp().ToString();
        var queryString = string.Join('&', parameters.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var signature = Sign(queryString);
        var fullQuery = $"{queryString}&signature={signature}";

        var url = method == HttpMethod.Get
            ? $"{endpoint}?{fullQuery}"
            : endpoint;

        using var request = new HttpRequestMessage(method, url);

        if (method != HttpMethod.Get)
            request.Content = new StringContent(fullQuery, Encoding.UTF8, "application/x-www-form-urlencoded");

        _logger.LogDebug("Binance {Method} {Endpoint}", method, endpoint);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Binance API error: {StatusCode} {Body}", response.StatusCode, body);
            throw new HttpRequestException($"Binance API error {response.StatusCode}: {body}");
        }

        return body;
    }

    public void Dispose() => _http.Dispose();
}

internal static class BinanceJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = false,
    };
}
