using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class BinanceLoadAssetResolver(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<HistoryLoaderOptions> options) : ILoadAssetResolver
{
    public Task<AssetCollectionConfig> Resolve(
        string exchange, string symbol, string assetType, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        var configured = opts.Assets.FirstOrDefault(a =>
            string.Equals(a.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Type, assetType, StringComparison.OrdinalIgnoreCase));

        if (configured is not null)
            return Task.FromResult(configured);

        return Synthesize(symbol, assetType, opts.Binance, ct);
    }

    private async Task<AssetCollectionConfig> Synthesize(
        string symbol, string assetType, BinanceOptions binance, CancellationToken ct)
    {
        // Sanitize before URL interpolation — endpoint validation already rejects traversal,
        // but uppercase normalization ensures the query parameter is safe regardless.
        var safeSymbol = symbol.ToUpperInvariant();
        var baseUrl = AssetTypes.IsFutures(assetType)
            ? binance.FuturesBaseUrl
            : binance.SpotBaseUrl;
        var path = AssetTypes.IsFutures(assetType)
            ? $"/fapi/v1/exchangeInfo?symbol={safeSymbol}"
            : $"/api/v3/exchangeInfo?symbol={safeSymbol}";

        var client = httpClientFactory.CreateClient("binance-archive");
        using var response = await client.GetAsync(baseUrl + path, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"exchangeInfo failed for {symbol}");

        var json = await response.Content.ReadAsStringAsync(ct);
        var decimalDigits = ParseDecimalDigits(json);

        return new AssetCollectionConfig
        {
            Symbol = safeSymbol,
            Type = assetType,
            DecimalDigits = decimalDigits,
            HistoryStart = new DateOnly(2017, 1, 1),
            Feeds = [],
        };
    }

    private static int ParseDecimalDigits(string exchangeInfoJson)
    {
        using var doc = JsonDocument.Parse(exchangeInfoJson);
        var symbols = doc.RootElement.GetProperty("symbols");
        foreach (var sym in symbols.EnumerateArray())
        {
            foreach (var filter in sym.GetProperty("filters").EnumerateArray())
            {
                if (filter.GetProperty("filterType").GetString() == "PRICE_FILTER")
                {
                    var tickSize = filter.GetProperty("tickSize").GetString() ?? "1";
                    return CountFractionalDigits(tickSize);
                }
            }
        }
        return 2;
    }

    private static int CountFractionalDigits(string tickSize)
    {
        var dotIdx = tickSize.IndexOf('.');
        if (dotIdx < 0) return 0;
        var fraction = tickSize.AsSpan(dotIdx + 1);
        var lastNonZero = -1;
        for (var i = 0; i < fraction.Length; i++)
            if (fraction[i] != '0') lastNonZero = i;
        return lastNonZero + 1;
    }
}
