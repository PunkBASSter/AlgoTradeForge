using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoTradeForge.Application.Live;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.Infrastructure.Live.Binance;

public sealed class BinanceApiClient : IExchangeOrderClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly byte[] _secretBytes;
    private readonly ILogger _logger;

    public BinanceApiClient(string baseRestUrl, string apiKey, string apiSecret, ILogger logger)
    {
        _logger = logger;
        _secretBytes = Encoding.UTF8.GetBytes(apiSecret);
        _http = new HttpClient { BaseAddress = new Uri(baseRestUrl) };
        _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", apiKey);
    }

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

    async Task<long> IExchangeOrderClient.PlaceOrderAsync(
        string symbol, string side, string type, decimal quantity,
        decimal? price, decimal? stopPrice, CancellationToken ct)
    {
        var response = await PlaceOrderAsync(symbol, side, type, quantity, price, stopPrice, ct);
        return response.OrderId;
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

    public async Task<string> CreateListenKeyAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/userDataStream");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("listenKey").GetString()!;
    }

    public async Task KeepAliveListenKeyAsync(string listenKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v3/userDataStream?listenKey={listenKey}");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
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

    internal string Sign(string queryString)
    {
        var hash = HMACSHA256.HashData(_secretBytes, Encoding.UTF8.GetBytes(queryString));
        return Convert.ToHexStringLower(hash);
    }

    private async Task<string> SendSignedAsync(
        HttpMethod method, string endpoint, Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        parameters["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
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
